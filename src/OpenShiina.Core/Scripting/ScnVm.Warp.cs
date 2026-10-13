// "Play from here" (OpenShiina Tools): a game started to show one line of a scenario. Its logos
// and title are gone past as if START were chosen (PlayFromModule). The first
// scenario the scripts read after it is asked for (a new game's first) is that scenario instead,
// with what comes before the line taken out: its message lines, speakers, voices, sounds and
// waits made comments, its other commands (backgrounds, characters, music) left to run, so the
// scene is set as the story sets it (but for what earlier scenarios and choices set). The reads
// of the scenario asked for give that one for the rest of the game, so that a save it makes
// loads it again; Ctrl (the games' skip) is held until the first text is shown, so that the
// scene's transitions go by at once. The game's saves stay in memory: its own are left as they
// are (a save of the scenario put in place would not load in the real game).

using System.Text;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // The line to play from (the scenario's name as text.tsv has it, the line from 1), until the
    // scripts read a scenario; the game's scenarios, so that other .TXT files are left alone
    private (string File, int Line)? m_playFrom;
    private HashSet<string>? m_playScenarios;
    // The scenario asked for that gives the one put in place (its name as the scripts ask), and that one
    private string? m_playAlias, m_playTarget;
    private int m_playLine;
    private byte[]? m_playData;
    // Ctrl held for the scripts: from the scenario put in place to the first text shown
    private bool m_playSkip;

    // The saves while they stay in memory (null: on disk, through the host)
    private Dictionary<string, byte[]?>? m_memorySaves;

    /// <summary>
    /// Plays from line <paramref name="line"/> (from 1) of the scenario <paramref name="file"/>
    /// ("MAK01_02A.TXT"), once the scripts read the first of <paramref name="scenarios"/> (a new
    /// game); the saves stay in memory from now on. The interpreter's thread.
    /// </summary>
    public void PlayFrom(string file, int line, IEnumerable<string> scenarios)
    {
        m_playFrom = (file, line);
        m_playScenarios = new HashSet<string>(scenarios, StringComparer.OrdinalIgnoreCase);
        m_memorySaves ??= new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scenario the scripts get for <paramref name="name"/> (a .TXT they read) when it is the
    /// one put in place, with the name of the scenario it is; null for any other. The first
    /// scenario they load (<paramref name="load"/>; not one whose length or presence they ask for)
    /// is the one put in place.
    /// </summary>
    private (string Name, byte[] Data)? PlayFromScenario(string name, bool load)
    {
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return null;
        string file = name[(name.LastIndexOfAny(['\\', '/']) + 1)..];
        if (m_playAlias == null)
        {
            if (m_playFrom is not { } from || m_playScenarios?.Contains(file) != true)
                return null;
            // Its folder is the one of the scenario asked for (or none)
            string folder = name[..(name.Length - file.Length)];
            m_playTarget = (ReadArchiveFile(folder + from.File) != null ? folder : "") + from.File;
            m_playLine = from.Line;
            // Asked for its length (START does before it loads one, and makes room for that
            // much): the length of the one to be put in place, else it was cut short at that
            if (!load)
                return PlayFromData() is { } length ? (m_playTarget, length) : null;
            m_playAlias = name;
            m_playFrom = null;
            m_playSkip = true;
        }
        if (!name.Equals(m_playAlias, StringComparison.OrdinalIgnoreCase))
            return null;
        return PlayFromData() is { } data ? (m_playTarget!, data) : null;
    }

    /// <summary>The scenario to put in place, as the scripts read it, made once (again after the translation changed).</summary>
    private byte[]? PlayFromData()
    {
        if (m_playData == null && ReadArchiveFile(m_playTarget!) is { } data)
            m_playData = FromLine(Translate(m_playTarget!, data), m_playLine);
        return m_playData;
    }

    /// <summary>
    /// A scenario (Shift-JIS and gaiji, as the scripts read it) with what comes before line
    /// <paramref name="line"/> made comments: message and speaker lines, and the commands that
    /// wait or sound ($WAIT, $VOICE, $SE); but the line's own speaker and voice just before it.
    /// The lines stay where they are.
    /// </summary>
    private static byte[] FromLine(byte[] data, int line)
    {
        var lines = new List<(int Start, int End)>();
        for (int start = 0; start < data.Length;)
        {
            int end = Array.IndexOf(data, (byte)'\n', start);
            end = end < 0 ? data.Length : end + 1;
            lines.Add((start, end));
            start = end;
        }
        // The line's speaker and voice: the speaker, voice and empty lines right before it
        int keepFrom = Math.Min(line - 1, lines.Count);
        while (keepFrom > 0 && Before(data.AsSpan(lines[keepFrom - 1].Start, lines[keepFrom - 1].End - lines[keepFrom - 1].Start)) is LineKind.Empty or LineKind.Speaker or LineKind.Voice)
            keepFrom--;
        var output = new List<byte>(data.Length);
        for (int i = 0; i < lines.Count; i++)
        {
            var text = data.AsSpan(lines[i].Start, lines[i].End - lines[i].Start);
            if (i < keepFrom && Before(text) is LineKind.Message or LineKind.Speaker or LineKind.Voice or LineKind.Waits)
            {
                // ";" and the line's end, the line itself gone (its bytes may be gaiji)
                output.Add((byte)';');
                int ending = text.Length;
                while (ending > 0 && text[ending - 1] is (byte)'\r' or (byte)'\n')
                    ending--;
                output.AddRange(text[ending..]);
            }
            else
                output.AddRange(text);
        }
        return output.ToArray();
    }

    private enum LineKind { Empty, Comment, Message, Speaker, Voice, Waits, Command }

    /// <summary>What a line before the one to play from is.</summary>
    private static LineKind Before(ReadOnlySpan<byte> text)
    {
        int at = 0;
        while (at < text.Length && text[at] is (byte)' ' or (byte)'\t')
            at++;
        var body = text[at..];
        if (body.Length == 0 || body[0] is (byte)'\r' or (byte)'\n')
            return LineKind.Empty;
        if (body[0] == (byte)';')
            return LineKind.Comment;
        // 【 (81 79)
        if (body.Length >= 2 && body[0] == 0x81 && body[1] == 0x79)
            return LineKind.Speaker;
        if (body[0] != (byte)'$')
            return LineKind.Message;
        if (IsCommand(body, "$VOICE"))
            return LineKind.Voice;
        return IsCommand(body, "$WAIT") || IsCommand(body, "$SE") ? LineKind.Waits : LineKind.Command;
    }

    private static bool IsCommand(ReadOnlySpan<byte> body, string command) =>
        body.Length > command.Length && Encoding.ASCII.GetString(body[..command.Length]).Equals(command, StringComparison.OrdinalIgnoreCase)
        && body[command.Length] is (byte)',' or (byte)' ' or (byte)'\r' or (byte)'\n';

    // The title was gone past once (a later TOPMENU.SCN, the game's own Title, shows it)
    private bool m_titleSkipped;

    /// <summary>
    /// A module the scripts start (loadmod): TOPMENU.SCN while a line waits to be played from is
    /// started at its new game instead of its logos and title, as when START is chosen. START.SCN
    /// has set the game up before it; the title's choices end in one block (it frees the title,
    /// sets the new game up, Gevnum = 1, launch.scn), which reads the choice from a word of the
    /// module ("store L_S, a[990]; goto block"... "load L_S, a[927]" after Gevnum = 1): that word
    /// is 0, the first choice (START). 17 of the 18 games tried have it; Ao no Juuai's TOPMENU
    /// calls its title instead (<see cref="SkipTitleCall"/>).
    /// </summary>
    private void PlayFromModule(ScnContext c, string file)
    {
        if (m_playFrom == null || m_titleSkipped || !file.EndsWith("topmenu.scn", StringComparison.OrdinalIgnoreCase))
            return;
        m_titleSkipped = true;
        if (NewGameBlock(ReadBytes(c.CodeBase, c.CodeSize)) is { } found)
        {
            Write32(c.Base + found.Choice, 0);
            c.Pc = c.Base + found.Block;
            return;
        }
        SkipTitleCall(c);
    }

    /// <summary>
    /// Ao no Juuai (v2.34): its TOPMENU.SCN calls the title (a gosub to START's), then by the
    /// choice loads the replay's module (b[43] 6) or the game's. The call becomes a jump to the
    /// instruction after it, so the game's module starts at once (a new game, b[43] 0 at boot).
    /// </summary>
    private void SkipTitleCall(ScnContext c)
    {
        int at = c.CodeBase, end = c.CodeBase + c.CodeSize;
        for (int i = 0; i < 8 && at < end; i++)
        {
            var instruction = Decode(c, at);
            // gosub (7 bytes, as a goto: 58 02, 84 = a label, the offset) and an "if" after it
            if (instruction.Op == 0x0267 && instruction.Length == 7 && at + 7 < end && Decode(c, at + 7).Op == 0x01F4)
            {
                m_decoded.Remove(at);
                WriteBytes(at, [0x58, 0x02, 0x84, .. BitConverter.GetBytes(at + 7 - c.Base)]);
                return;
            }
            at += instruction.Length;
        }
    }

    /// <summary>The new game's block in a TOPMENU.SCN and the word it reads the title's choice from (offsets), or null.</summary>
    private static (int Block, int Choice)? NewGameBlock(byte[] code)
    {
        // "load L_S, a[927]" (02 03, 84 = a label, a[927] = 0A 9F 03) just after "mov 1, @Gevnum"
        int choice = -1;
        byte[] gevnum = "Gevnum"u8.ToArray();
        for (int i = 0; i + 10 <= code.Length && choice < 0; i++)
            if (code[i] == 0x02 && code[i + 1] == 0x03 && code[i + 2] == 0x84 && code[i + 7] == 0x0A && code[i + 8] == 0x9F && code[i + 9] == 0x03
                && code.AsSpan(Math.Max(0, i - 32), Math.Min(32, i)).IndexOf(gevnum) >= 0)
                choice = BitConverter.ToInt32(code, i + 3);
        if (choice < 0)
            return null;
        // "store L_S, choice" (03 03 84 S, then the choice) and "goto block" (58 02 84 block)
        var blocks = new Dictionary<int, int>();
        for (int i = 0; i + 7 <= code.Length; i++)
        {
            if (code[i] != 0x03 || code[i + 1] != 0x03 || code[i + 2] != 0x84 || BitConverter.ToInt32(code, i + 3) != choice)
                continue;
            for (int k = i + 8; k + 7 <= code.Length && k <= i + 20; k++)
                if (code[k] == 0x58 && code[k + 1] == 0x02 && code[k + 2] == 0x84)
                {
                    int block = BitConverter.ToInt32(code, k + 3);
                    blocks[block] = blocks.GetValueOrDefault(block) + 1;
                    break;
                }
        }
        if (blocks.Count == 0)
            return null;
        int best = blocks.MaxBy(b => b.Value).Key;
        return best > 0 && best < code.Length && choice < code.Length - 4 ? (best, choice) : null;
    }

    /// <summary>The first text the scripts show after the scenario was put in place: Ctrl let go.</summary>
    private void PlayFromShown()
    {
        if (m_playSkip)
            m_playSkip = false;
    }

    /// <summary>A key held, as the scripts see it: Ctrl too while a scene is skipped to the line to play from.</summary>
    private bool KeyHeld(int virtualKey) => m_host.KeyDown(virtualKey) || m_playSkip && virtualKey is 0x11 or 0xA2;

    private byte[]? ReadSave(string name) =>
        m_memorySaves is { } saves ? saves.TryGetValue(name, out var data) ? data : m_host.ReadSaveFile(name) : m_host.ReadSaveFile(name);

    private void WriteSave(string name, byte[] data)
    {
        if (m_memorySaves is { } saves)
            saves[name] = data;
        else
            m_host.WriteSaveFile(name, data);
    }

    private void DeleteSave(string name)
    {
        if (m_memorySaves is { } saves)
            saves[name] = null;
        else
            m_host.DeleteSaveFile(name);
    }
}
