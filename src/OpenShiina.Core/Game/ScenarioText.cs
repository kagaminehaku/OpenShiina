// The text of a game's scenarios for a translation (OpenShiina Tools). Export puts every message
// line of the scenario files (the .TXT entries of the game's archives) into a table to translate;
// Build turns the table, translated, into the translation\ folder the players read in place of the
// archives' files (ScnVm.Files; ScnGaiji draws what Shift-JIS lacks).
//
//   text.tsv     id, speaker, original, translation: a message line a row; the id is the file
//                and the line ("PRO01_01.TXT:36"), the speaker the name of the line before it
//   names.tsv    original, translation: the speakers' names (inside 【】). The scenarios keep
//                the original names (games find voices and name plates by them); the players
//                draw the translation through strings.txt
//   strings.tsv  original, translation: other texts the scripts draw from their own code
//                (choices, menus), added by hand; into strings.txt as the names
//
// The tables are UTF-8 with tabs between the fields, a header row first. A field that starts and
// ends with a double quote is read as spreadsheets write one (its quotes doubled inside), so the
// files can go through Excel or LibreOffice and back. An empty translation keeps the original.

using System.Text;

namespace OpenShiina.Game;

public static class ScenarioText
{
    public const string TextTable = "text.tsv", NameTable = "names.tsv", StringTable = "strings.tsv";

    /// <summary>The folder Build writes, as the game folder takes it.</summary>
    public const string TranslationFolder = "translation";

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

    /// <summary>A line of a scenario: its text (no line end), what it is, and the speaker's name for a message.</summary>
    private readonly record struct Line(string Text, string Ending, LineKind Kind, string Speaker);

    private enum LineKind { Other, Speaker, Message }

    /// <summary>A scenario's lines (Shift-JIS, or UTF-8 after a byte order mark), the speaker of each message.</summary>
    private static List<Line> Lines(byte[] data)
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

    public sealed record ExportResult(int Files, int Lines, int Names);

    /// <summary>
    /// Writes text.tsv and names.tsv (and an empty strings.tsv when there is none) into
    /// <paramref name="outputFolder"/>, keeping the translations of tables already there for
    /// lines whose original is the same.
    /// </summary>
    public static ExportResult Export(IReadOnlyList<(string Name, byte[] Data)> scenarios, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var oldText = ReadTable(Path.Combine(outputFolder, TextTable), 4).ToDictionary(r => r[0], r => (Original: r[2], Translation: r[3]));
        var oldNames = TranslationsOf(Path.Combine(outputFolder, NameTable));
        var text = new StringBuilder("id\tspeaker\toriginal\ttranslation\n");
        var names = new List<string>();
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        int files = 0, rows = 0;
        foreach (var (name, data) in scenarios)
        {
            bool any = false;
            var lines = Lines(data);
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Kind == LineKind.Speaker && SpeakerName(line.Text.Trim()) is { Length: > 0 } speaker && nameSet.Add(speaker))
                    names.Add(speaker);
                if (line.Kind != LineKind.Message)
                    continue;
                string id = $"{name}:{i + 1}";
                string kept = oldText.TryGetValue(id, out var old) && old.Original == Clean(line.Text) ? old.Translation : "";
                text.Append(Field(id)).Append('\t').Append(Field(line.Speaker)).Append('\t')
                    .Append(Field(line.Text)).Append('\t').Append(Field(kept)).Append('\n');
                rows++;
                any = true;
            }
            if (any)
                files++;
        }
        WriteUtf8(Path.Combine(outputFolder, TextTable), text.ToString());
        var nameTable = new StringBuilder("original\ttranslation\n");
        foreach (var name in names)
            nameTable.Append(Field(name)).Append('\t').Append(Field(oldNames.GetValueOrDefault(name, ""))).Append('\n');
        WriteUtf8(Path.Combine(outputFolder, NameTable), nameTable.ToString());
        string strings = Path.Combine(outputFolder, StringTable);
        if (!File.Exists(strings))
            WriteUtf8(strings, "original\ttranslation\n");
        return new ExportResult(files, rows, names.Count);
    }

    public sealed record BuildResult(int Files, int Lines, int Strings, int CodesUsed, int Missing, List<string> Problems);

    /// <summary>
    /// Reads the tables of <paramref name="tableFolder"/> and writes into
    /// <paramref name="outputFolder"/>\translation the scenarios with a translated line (UTF-8 with
    /// a byte order mark, the other lines as they were) and strings.txt (names and strings).
    /// </summary>
    public static BuildResult Build(IReadOnlyList<(string Name, byte[] Data)> scenarios, string tableFolder, string outputFolder)
    {
        var problems = new List<string>();
        // file -> line number -> (original, translation)
        var byFile = new Dictionary<string, Dictionary<int, (string Original, string Translation)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTable(Path.Combine(tableFolder, TextTable), 4))
        {
            if (row[3].Trim().Length == 0)
                continue;
            int colon = row[0].LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(row[0][(colon + 1)..], out int number))
            {
                problems.Add($"{row[0]}: not an id of text.tsv (FILE.TXT:line)");
                continue;
            }
            string file = row[0][..colon];
            if (!byFile.TryGetValue(file, out var lines))
                byFile[file] = lines = new();
            lines[number] = (row[2], row[3]);
        }

        string target = Path.Combine(outputFolder, TranslationFolder);
        Directory.CreateDirectory(target);
        var gaiji = new ScnGaiji();
        int files = 0, translated = 0;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in scenarios)
        {
            known.Add(name);
            if (!byFile.TryGetValue(name, out var rows))
                continue;
            var lines = Lines(data);
            var output = new StringBuilder("﻿");
            int done = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                string text = line.Text;
                if (rows.Remove(i + 1, out var row))
                {
                    if (line.Kind != LineKind.Message || Clean(line.Text) != row.Original)
                        problems.Add($"{name}:{i + 1}: the game's line is not the table's original (another version of the game?); kept the game's");
                    else
                    {
                        text = Clean(row.Translation);
                        done++;
                    }
                }
                output.Append(text).Append(line.Ending);
            }
            foreach (int number in rows.Keys)
                problems.Add($"{name}:{number}: no such line");
            if (done == 0)
                continue;
            byte[] bytes = Encoding.UTF8.GetBytes(output.ToString());
            File.WriteAllBytes(MediaConverter.SafeOutputPath(target, name), bytes);
            gaiji.Scenario(bytes);
            files++;
            translated += done;
        }
        foreach (var file in byFile.Keys.Where(f => !known.Contains(f)))
            problems.Add($"{file}: the game has no such scenario");

        // strings.txt: the names, then the strings written by hand
        var strings = new StringBuilder();
        int count = 0;
        foreach (var table in new[] { NameTable, StringTable })
            foreach (var (original, translation) in TranslationsOf(Path.Combine(tableFolder, table)))
            {
                if (translation.Trim().Length == 0 || original.Length == 0)
                    continue;
                strings.Append(Clean(original)).Append('\t').Append(Clean(translation)).Append('\n');
                gaiji.Text(Clean(translation));
                count++;
            }
        if (count > 0)
            WriteUtf8(Path.Combine(target, "strings.txt"), strings.ToString());
        return new BuildResult(files, translated, count, gaiji.Used, gaiji.Missing, problems);
    }

    /// <summary>A field's text on one line: tabs and line ends as spaces.</summary>
    private static string Clean(string text) => text.Replace("\r\n", " ").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>A field as written: on one line, quoted when it has a double quote.</summary>
    private static string Field(string text)
    {
        text = Clean(text);
        return text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    private static void WriteUtf8(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>The (original, translation) rows of a two-field table, empty when there is no file.</summary>
    private static Dictionary<string, string> TranslationsOf(string path)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in ReadTable(path, 2))
            rows.TryAdd(row[0], row[1]);
        return rows;
    }

    /// <summary>
    /// The rows of a table after its header, each with <paramref name="fields"/> fields (missing
    /// ones empty); none when there is no file.
    /// </summary>
    private static List<string[]> ReadTable(string path, int fields)
    {
        var rows = new List<string[]>();
        if (!File.Exists(path))
            return rows;
        string text = File.ReadAllText(path, Encoding.UTF8);
        var row = new List<string>();
        var field = new StringBuilder();
        bool header = true;
        int i = 0;
        void EndRow()
        {
            if (header)
                header = false;
            else if (row.Count > 1 || row[0].Length > 0)
            {
                while (row.Count < fields)
                    row.Add("");
                rows.Add(row.Take(fields).ToArray());
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
        return rows;
    }
}
