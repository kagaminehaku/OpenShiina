// A translation as tables, kept in the game folder's translation\ and read by the player as they
// are (OpenShiina Tools makes and edits them):
//
//   text.tsv     id, speaker, original, translation: a message line of the scenarios a row; the
//                id is the scenario file and the line ("PRO01_01.TXT:36"), the speaker the name
//                of the line before it
//   names.tsv    original, translation: the speakers' names (inside 【】). The scenarios keep
//                the original names (games find voices and name plates by them); the player draws
//                the translation in their place, as a string
//   strings.tsv  original, translation: other texts the scripts draw from their own code
//                (choices, menus), drawn translated when the whole text is the original
//
// The tables are UTF-8 with tabs between the fields, a header row first; more fields after
// those are kept as they are (notes, who translated a line). A field that starts and ends with a
// double quote is read as spreadsheets write one (its quotes doubled inside), so the files can go
// through Excel or LibreOffice and back. An empty translation keeps the original.
//
// The player (ScnVm.Files) applies text.tsv as it reads a scenario: the lines whose original is
// the game's are replaced, and the scenario goes on as a translated one (UTF-8, ScnGaiji). A
// scenario file of its own in translation\ (UTF-8 with a byte order mark) comes first.

using System.Text;

namespace OpenShiina.Game;

/// <summary>A message line of text.tsv.</summary>
public sealed class TextRow
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public string Speaker { get; init; } = "";
    public required string Original { get; init; }
    public string Translation { get; set; } = "";
    /// <summary>The fields after the translation, kept as they were.</summary>
    public string[] Extra { get; set; } = [];

    public string Id => $"{File}:{Line}";
}

/// <summary>A row of names.tsv or strings.tsv.</summary>
public sealed class StringRow
{
    public required string Original { get; set; }
    public string Translation { get; set; } = "";
    public string[] Extra { get; set; } = [];
}

/// <summary>The three tables of a translation.</summary>
public sealed class TranslationTables
{
    public const string TextTable = "text.tsv", NameTable = "names.tsv", StringTable = "strings.tsv";

    /// <summary>The folder of a game's translation, inside the game's folder.</summary>
    public const string Folder = "translation";

    public List<TextRow> Lines { get; } = new();
    public List<StringRow> Names { get; } = new();
    public List<StringRow> Strings { get; } = new();

    // Headers' fields after the known ones (extra columns), kept for writing back
    private string[] m_textExtra = [], m_nameExtra = [], m_stringExtra = [];

    // Translated lines by scenario file (upper case) and line, made on first use
    private Dictionary<string, Dictionary<int, TextRow>>? m_byFile;

    /// <summary>A translation's tables exist in <paramref name="folder"/>.</summary>
    public static bool Exist(string folder) => File.Exists(Path.Combine(folder, TextTable));

    /// <summary>The tables of a folder (the missing ones empty).</summary>
    public static TranslationTables ReadFolder(string folder) =>
        Read(name => Path.Combine(folder, name) is var path && File.Exists(path) ? File.ReadAllBytes(path) : null);

    /// <summary>The tables a reader gives by their names (null for a missing one).</summary>
    public static TranslationTables Read(Func<string, byte[]?> file)
    {
        var tables = new TranslationTables();
        if (file(TextTable) is { } text)
        {
            var rows = Tsv.Read(Tsv.Decode(text), 4, out tables.m_textExtra);
            foreach (var row in rows)
            {
                int colon = row[0].LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(row[0][(colon + 1)..], out int line))
                    continue;
                tables.Lines.Add(new TextRow
                {
                    File = row[0][..colon], Line = line, Speaker = row[1], Original = row[2], Translation = row[3], Extra = row[4..],
                });
            }
        }
        if (file(NameTable) is { } names)
            tables.Names.AddRange(StringRows(Tsv.Read(Tsv.Decode(names), 2, out tables.m_nameExtra)));
        if (file(StringTable) is { } strings)
            tables.Strings.AddRange(StringRows(Tsv.Read(Tsv.Decode(strings), 2, out tables.m_stringExtra)));
        return tables;
    }

    private static IEnumerable<StringRow> StringRows(List<string[]> rows) =>
        rows.Where(r => r[0].Length > 0 && !r[0].StartsWith('#')).Select(r => new StringRow { Original = r[0], Translation = r[1], Extra = r[2..] });

    /// <summary>Writes the three tables into <paramref name="folder"/>.</summary>
    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        Tsv.Write(Path.Combine(folder, TextTable), ["id", "speaker", "original", "translation", .. m_textExtra],
            Lines.Select(l => (string[])[l.Id, l.Speaker, l.Original, l.Translation, .. l.Extra]));
        Tsv.Write(Path.Combine(folder, NameTable), ["original", "translation", .. m_nameExtra],
            Names.Select(n => (string[])[n.Original, n.Translation, .. n.Extra]));
        Tsv.Write(Path.Combine(folder, StringTable), ["original", "translation", .. m_stringExtra],
            Strings.Select(s => (string[])[s.Original, s.Translation, .. s.Extra]));
        m_byFile = null;
    }

    /// <summary>
    /// <paramref name="scenario"/> (the Shift-JIS file of that name) with the lines text.tsv
    /// translates, as a translated scenario (UTF-8 with a byte order mark); null when it has
    /// none (or none whose original is the game's line).
    /// </summary>
    public byte[]? Apply(string fileName, byte[] scenario) => Apply(fileName, scenario, null);

    private byte[]? Apply(string fileName, byte[] scenario, List<string>? problems)
    {
        m_byFile ??= Lines.Where(l => l.Translation.Trim().Length > 0)
            .GroupBy(l => l.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(l => l.Line).ToDictionary(x => x.Key, x => x.First()), StringComparer.OrdinalIgnoreCase);
        if (!m_byFile.TryGetValue(fileName, out var rows))
            return null;
        var lines = ScenarioText.Lines(scenario);
        var output = new StringBuilder("﻿");
        int done = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string text = line.Text;
            if (rows.TryGetValue(i + 1, out var row))
            {
                if (line.Kind == ScenarioText.LineKind.Message && Tsv.Clean(line.Text) == row.Original)
                {
                    text = Tsv.Clean(row.Translation);
                    done++;
                }
                else
                    problems?.Add($"{row.Id}: the game's line is not the table's original (another version of the game?): kept the game's");
            }
            output.Append(text).Append(line.Ending);
        }
        if (problems != null)
            foreach (var row in rows.Values.Where(r => r.Line > lines.Count))
                problems.Add($"{row.Id}: the scenario has no such line");
        return done > 0 ? Encoding.UTF8.GetBytes(output.ToString()) : null;
    }

    /// <summary>The names and strings with a translation, names first.</summary>
    public IEnumerable<(string Original, string Translation)> TranslatedStrings() =>
        Names.Concat(Strings).Where(s => s.Original.Length > 0 && s.Translation.Trim().Length > 0)
            .Select(s => (Tsv.Clean(s.Original), Tsv.Clean(s.Translation)));

    public sealed record Summary(int Lines, int Translated, int Files, int Strings, int CodesUsed, int Missing, List<string> Problems);

    /// <summary>
    /// How far the translation is and what the player will make of it: the lines translated,
    /// the gaiji codes the translation takes (ScnGaiji.Capacity in all), the rows that do not fit
    /// the game's scenarios.
    /// </summary>
    public Summary Check(IReadOnlyList<(string Name, byte[] Data)> scenarios)
    {
        var problems = new List<string>();
        var gaiji = new ScnGaiji();
        int files = 0;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in scenarios)
        {
            known.Add(name);
            if (Apply(name, data, problems) is { } translated)
            {
                gaiji.Scenario(translated);
                files++;
            }
        }
        foreach (var file in Lines.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !known.Contains(f)))
            problems.Add($"{file}: the game has no such scenario");
        int strings = 0;
        foreach (var (_, translation) in TranslatedStrings())
        {
            gaiji.Text(translation);
            strings++;
        }
        return new Summary(Lines.Count, Lines.Count(l => l.Translation.Trim().Length > 0), files, strings, gaiji.Used, gaiji.Missing, problems);
    }
}

public static class ScenarioText
{
    /// <summary>
    /// The scenario files of the game in <paramref name="folder"/>: (entry name, Shift-JIS
    /// bytes), each name once (the first archive's, as the players find it), archives by name.
    /// </summary>
    public static List<(string Name, byte[] Data)> ReadScenarios(string folder, EncryptionScheme scheme, Action<string>? progress = null)
    {
        var scenarios = new List<(string, byte[])>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(folder, "*.war", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            using var view = new ArcView(path);
            WarcArchive? warc;
            try
            {
                warc = WarcOpener.TryOpen(view, scheme);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                warc = null;
            }
            if (warc == null)
                continue;
            using (warc)
            {
                foreach (var entry in warc.Entries)
                {
                    string name = Path.GetFileName(entry.Name.Replace('\\', '/'));
                    if (!name.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                        continue;
                    progress?.Invoke(name);
                    scenarios.Add((name, WarcOpener.OpenEntry(warc, entry)));
                }
            }
        }
        return scenarios;
    }

    /// <summary>A line of a scenario: its text (no line end), its line end, what it is, and the speaker's name for a message.</summary>
    internal readonly record struct Line(string Text, string Ending, LineKind Kind, string Speaker);

    internal enum LineKind { Other, Speaker, Message }

    /// <summary>A scenario's lines (Shift-JIS, or UTF-8 after a byte order mark), the speaker of each message.</summary>
    internal static List<Line> Lines(byte[] data)
    {
        string text = ScnGaiji.IsUtf8(data) ? Encoding.UTF8.GetString(data, 3, data.Length - 3) : Encodings.cp932.GetString(data);
        var lines = new List<Line>();
        string speaker = "";
        for (int start = 0; start < text.Length;)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end + 1;
            int textEnd = end;
            while (textEnd > start && text[textEnd - 1] is '\r' or '\n')
                textEnd--;
            string line = text[start..textEnd];
            string body = line.Trim();
            var kind = body.Length == 0 || body[0] is ';' or '$' ? LineKind.Other : body[0] == '【' ? LineKind.Speaker : LineKind.Message;
            if (kind == LineKind.Speaker)
                speaker = SpeakerName(body);
            lines.Add(new Line(line, text[textEnd..end], kind, kind == LineKind.Message ? speaker : ""));
            if (kind == LineKind.Message)
                speaker = "";
            start = end;
        }
        return lines;
    }

    /// <summary>The name in a speaker line ("【土筆】" -> "土筆").</summary>
    private static string SpeakerName(string body)
    {
        int close = body.IndexOf('】');
        return close > 1 ? body[1..close] : body[1..];
    }

    public sealed record ExportResult(int Files, int Lines, int Names, int Kept);

    /// <summary>
    /// The game's message lines and speakers' names into the tables of <paramref name="folder"/>:
    /// made there, or brought up to date with the game (the translations, and fields after them,
    /// kept for lines whose original is the same; strings.tsv as it was).
    /// </summary>
    public static ExportResult Export(IReadOnlyList<(string Name, byte[] Data)> scenarios, string folder)
    {
        var old = TranslationTables.ReadFolder(folder);
        var oldLines = old.Lines.GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var oldNames = old.Names.GroupBy(n => n.Original).ToDictionary(g => g.Key, g => g.First());
        var tables = old;
        var lines = new List<TextRow>();
        var names = new List<StringRow>();
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        int files = 0, kept = 0;
        foreach (var (name, data) in scenarios)
        {
            bool any = false;
            var scenario = Lines(data);
            for (int i = 0; i < scenario.Count; i++)
            {
                var line = scenario[i];
                if (line.Kind == LineKind.Speaker && SpeakerName(line.Text.Trim()) is { Length: > 0 } speaker && nameSet.Add(speaker))
                    names.Add(oldNames.GetValueOrDefault(speaker) ?? new StringRow { Original = speaker });
                if (line.Kind != LineKind.Message)
                    continue;
                string original = Tsv.Clean(line.Text);
                var row = new TextRow { File = name, Line = i + 1, Speaker = line.Speaker, Original = original };
                if (oldLines.TryGetValue(row.Id, out var was) && was.Original == original)
                {
                    row.Translation = was.Translation;
                    row.Extra = was.Extra;
                    if (was.Translation.Length > 0)
                        kept++;
                }
                lines.Add(row);
                any = true;
            }
            if (any)
                files++;
        }
        // Names the table had that the scenarios no longer use stay, after the others
        names.AddRange(old.Names.Where(n => !nameSet.Contains(n.Original) && n.Translation.Length > 0));
        tables.Lines.Clear();
        tables.Lines.AddRange(lines);
        tables.Names.Clear();
        tables.Names.AddRange(names);
        tables.Write(folder);
        return new ExportResult(files, lines.Count, names.Count, kept);
    }
}

/// <summary>Tab-separated tables as OpenShiina writes them and spreadsheets save them.</summary>
public static class Tsv
{
    /// <summary>A field's text on one line: tabs and line ends as spaces.</summary>
    public static string Clean(string text) => text.Replace("\r\n", " ").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>A field as written: on one line, quoted when it has a double quote.</summary>
    public static string Field(string text)
    {
        text = Clean(text);
        return text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>A table file's text: UTF-8, or what its byte order mark says (Excel saves UTF-16).</summary>
    public static string Decode(byte[] file)
    {
        using var reader = new StreamReader(new MemoryStream(file), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static void Write(string path, string[] header, IEnumerable<string[]> rows)
    {
        var text = new StringBuilder();
        text.AppendJoin('\t', header.Select(Field)).Append('\n');
        foreach (var row in rows)
            text.AppendJoin('\t', row.Select(Field)).Append('\n');
        // Written whole then moved in place, so a table is never left half written
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The rows of a table after its header, each with at least <paramref name="fields"/> fields
    /// (missing ones empty); <paramref name="extraHeader"/>: the header's fields after those.
    /// </summary>
    public static List<string[]> Read(string text, int fields, out string[] extraHeader)
    {
        var rows = new List<string[]>();
        extraHeader = [];
        var row = new List<string>();
        var field = new StringBuilder();
        bool header = true;
        string[] headerFields = [];
        int i = 0;
        void EndRow()
        {
            if (header)
            {
                header = false;
                headerFields = row.ToArray();
            }
            else if (row.Count > 1 || row[0].Length > 0)
            {
                while (row.Count < fields)
                    row.Add("");
                rows.Add(row.ToArray());
            }
            row = new List<string>();
        }
        while (i <= text.Length)
        {
            // A field: quoted when it starts with a quote and the closing one ends it
            field.Clear();
            int end = -1;
            if (i < text.Length && text[i] == '"')
            {
                int j = i + 1;
                var quoted = new StringBuilder();
                while (j < text.Length)
                {
                    if (text[j] == '"')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '"')
                        {
                            quoted.Append('"');
                            j += 2;
                            continue;
                        }
                        break;
                    }
                    quoted.Append(text[j++]);
                }
                if (j < text.Length && (j + 1 == text.Length || text[j + 1] is '\t' or '\r' or '\n'))
                {
                    field.Append(quoted);
                    end = j + 1;
                }
            }
            if (end < 0)
            {
                end = i;
                while (end < text.Length && text[end] is not ('\t' or '\r' or '\n'))
                    end++;
                field.Append(text, i, end - i);
            }
            row.Add(field.ToString());
            if (end >= text.Length)
            {
                EndRow();
                break;
            }
            if (text[end] == '\t')
            {
                i = end + 1;
                continue;
            }
            i = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            EndRow();
            if (i == text.Length)
                break;
        }
        if (headerFields.Length > fields)
            extraHeader = headerFields[fields..];
        return rows;
    }
}
