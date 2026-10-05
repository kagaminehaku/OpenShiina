// Scenario .TXT files (Shift-JIS) as the engine reads them at run time; see docs/engine-notes.md.
//
//   $NAME,arg,arg      command (a ';' starts a comment)
//   【name】           speaker of the next text line
//   text               one message; every non-empty line is shown on its own click
//   ;...               comment

namespace OpenShiina.Scripting;

/// <summary>One executable line: a command, or a message with its speaker.</summary>
public sealed record ScriptLine(int LineNumber, string? Command, string[] Args, string? Speaker, string? Text)
{
    public string Arg(int index) => index < Args.Length ? Args[index] : "";

    public int IntArg(int index, int fallback = 0) =>
        index < Args.Length && int.TryParse(Args[index], out int v) ? v : fallback;
}

public sealed class ScenarioScript
{
    public string Name { get; }
    public IReadOnlyList<ScriptLine> Lines { get; }

    /// <summary>$LABEL number -> index of that $LABEL line.</summary>
    public IReadOnlyDictionary<int, int> Labels { get; }

    private ScenarioScript(string name, List<ScriptLine> lines, Dictionary<int, int> labels)
    {
        Name = name;
        Lines = lines;
        Labels = labels;
    }

    public static ScenarioScript Parse(string name, string text)
    {
        var lines = new List<ScriptLine>();
        var labels = new Dictionary<int, int>();
        string? speaker = null;
        int number = 0;

        foreach (var raw in text.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';')
                continue;

            if (line[0] == '$')
            {
                int comment = line.IndexOf(';');
                if (comment >= 0)
                    line = line[..comment];
                var parts = line[1..].Split(',').Select(p => p.Trim()).ToArray();
                if (parts[0].Length == 0)
                    continue;
                var command = new ScriptLine(number, parts[0].ToUpperInvariant(), parts[1..], null, null);
                if (command.Command == "LABEL")
                    labels[command.IntArg(0)] = lines.Count;
                lines.Add(command);
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith('【') && trimmed.EndsWith('】'))
            {
                speaker = trimmed[1..^1];
                continue;
            }
            lines.Add(new ScriptLine(number, null, Array.Empty<string>(), speaker, trimmed));
            speaker = null;
        }
        return new ScenarioScript(name, lines, labels);
    }

    /// <summary>Text as shown on screen: the font character for the engine's gaiji codes.</summary>
    public static string DisplayText(string text) => text.Replace('①', '♥');
}
