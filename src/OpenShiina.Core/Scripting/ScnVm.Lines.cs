// Which line of which scenario the game is showing, for OpenShiina Tools to go to it in the
// translation's table ("MAK00.TXT:28", text.tsv's id). The scripts do not show a message from
// the scenario in memory: Maki Fes!'s START copies the line into a buffer of its own, with "_r"
// where it breaks it, and makes half-width characters full-width. So the text handed to the text
// system (SetText) is looked for among the message lines of the scenarios read last, compared
// with their widths folded (NFKC), a translation's gaiji codes as they are, and up to its first
// control code.

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    private const int RecentScenarios = 4;
    // The shortest text looked for anywhere (a name of two letters could start a message line),
    // and how far after the line shown last a shorter one is looked for
    private const int AnywhereChars = 6, NearLines = 40;
    private const int KeyChars = 24;

    private sealed class RecentScenario(string file, byte[] data)
    {
        public string File { get; } = file;
        public byte[] Data { get; } = data;
        // The lines as compared, null for a line that is not a message; made when first looked in
        public string?[]? Lines;
    }

    // The scenarios the scripts read last, the newest first
    private readonly List<RecentScenario> m_recentScenarios = new();
    private RecentScenario? m_shownScenario;
    private int m_shownIndex = -1;

    /// <summary>
    /// A message line of a scenario was shown ("MAK00.TXT:28"), each time it changes. Raised on
    /// the interpreter's thread; the lines are looked for only while something listens.
    /// </summary>
    public event Action<string>? LineShown;

    /// <summary>The scenario line shown last ("MAK00.TXT:28"), or null.</summary>
    public string? ShownLine { get; private set; }

    /// <summary>A scenario the scripts read, as they have it (a translation's in Shift-JIS and gaiji).</summary>
    private void NoteScenario(string name, byte[] data)
    {
        // As text.tsv names it: the archive's entry, upper case (the scripts may ask in lower case)
        string file = name[(name.LastIndexOfAny(['\\', '/']) + 1)..].ToUpperInvariant();
        int at = m_recentScenarios.FindIndex(s => s.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (at == 0 && m_recentScenarios[0].Data == data)
            return;
        var scenario = at >= 0 && m_recentScenarios[at].Data == data ? m_recentScenarios[at] : new RecentScenario(file, data);
        if (at >= 0)
            m_recentScenarios.RemoveAt(at);
        m_recentScenarios.Insert(0, scenario);
        if (m_recentScenarios.Count > RecentScenarios)
            m_recentScenarios.RemoveAt(RecentScenarios);
    }

    /// <summary>The scripts hand the text system a text: if it is a scenario's message line, that line is the one shown.</summary>
    private void NoteShownText(int text)
    {
        if (LineShown == null && !m_playSkip || text == 0)
            return;
        int length = Math.Min(StringLength(text), 4096);
        string key = Key(Comparable(ReadBytes(text, length)));
        if (key.Length < 2)
            return;
        PlayFromShown();
        if (LineShown == null || m_recentScenarios.Count == 0)
            return;
        // Near after the line shown last first (the story goes on), then anywhere
        if (m_shownScenario != null && Find(m_shownScenario, key, m_shownIndex + 1, NearLines) is >= 0 and int near)
        {
            Shown(m_shownScenario, near);
            return;
        }
        if (key.Length < AnywhereChars)
            return;
        foreach (var scenario in m_recentScenarios)
            if (Find(scenario, key, 0, int.MaxValue) is >= 0 and int index)
            {
                Shown(scenario, index);
                return;
            }
    }

    private void Shown(RecentScenario scenario, int index)
    {
        m_shownScenario = scenario;
        m_shownIndex = index;
        string id = $"{scenario.File}:{index + 1}";
        if (id == ShownLine)
            return;
        ShownLine = id;
        LineShown?.Invoke(id);
    }

    /// <summary>The first message line from <paramref name="from"/> on (at most <paramref name="count"/> lines) that starts with the key, or -1.</summary>
    private int Find(RecentScenario scenario, string key, int from, int count)
    {
        var lines = scenario.Lines ??= ScenarioLines(scenario.Data);
        int end = (int)Math.Min(lines.Length, (long)Math.Max(from, 0) + count);
        for (int i = Math.Max(from, 0); i < end; i++)
            if (lines[i] is { } line && line.StartsWith(key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>A scenario's lines as compared: its message lines, null for the others (comments, commands, speakers, empty).</summary>
    private string?[] ScenarioLines(byte[] data)
    {
        var lines = new List<string?>();
        for (int start = 0; start <= data.Length;)
        {
            int end = Array.IndexOf(data, (byte)'\n', start);
            if (end < 0)
                end = data.Length;
            string line = Comparable(data.AsSpan(start, end - start)).Trim();
            lines.Add(line.Length == 0 || line[0] is ';' or '$' or '【' ? null : line);
            start = end + 1;
        }
        return lines.ToArray();
    }

    /// <summary>The start of a text as it is looked for: up to its first control code ("_r"), its first characters.</summary>
    private static string Key(string text)
    {
        int control = text.IndexOf('_');
        if (control >= 0)
            text = text[..control];
        text = text.Trim();
        return text.Length > KeyChars ? text[..KeyChars] : text;
    }

    /// <summary>Shift-JIS text with its widths folded (NFKC); a gaiji code as its own two characters.</summary>
    private string Comparable(ReadOnlySpan<byte> text)
    {
        var s = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            byte b = text[i];
            if (b == 0)
                break;
            if (b is (byte)'\r' or (byte)'\n')
                continue;
            if (IsLead(b) && i + 1 < text.Length)
            {
                int code = b << 8 | text[i + 1];
                if (m_gaiji.KindOf(code) != ScnGaiji.Kind.None)
                    s.Append('').Append((char)code);
                else
                    s.Append(Encodings.cp932.GetString(text.Slice(i, 2)));
                i++;
            }
            else
                s.Append(b < 0x80 ? (char)b : Encodings.cp932.GetString(text.Slice(i, 1))[0]);
        }
        return s.ToString().Normalize(NormalizationForm.FormKC);
    }
}
