// ScnTools arccheck <game folder>: whether every file of a game's archives comes out whole with
// its scheme, before playing it. Per archive: its entries, those Decrypt2 runs on (flags
// 0x40000000 after unpacking, 0x20000000 before), the S25 / TXT / Ogg files checked
// (FileChecks) and the ones that are not whole; then the files whose bytes use DecodeBin's date
// (a wrong date spoils exactly those: run datefind), and the names found in more than one archive
// (the engine takes such a file from the first archive the scripts mount: GameData.Mount).
// Exit code 0 when every file checked is whole.
static class ArcCheck
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("usage: ScnTools arccheck <game folder>");
            return 1;
        }
        using var game = GameArchives.Open(args[0], out string? why);
        if (game == null)
        {
            Console.WriteLine($"{args[0]}: {why}");
            return 3;
        }
        Console.WriteLine($"{game.Scheme.Name} (scheme from {game.SchemeSource}), engine v{(game.EngineVersion.Length > 0 ? game.EngineVersion : "?")}");
        uint date = game.Scheme.DecodeBin is { Length: >= 0x1024 } bin ? BitConverter.ToUInt32(bin, 0x1020) : 0;
        Console.WriteLine(game.Scheme.DecodeBin == null ? "  DecodeBin: none" : $"  DecodeBin date: {date:X8}");
        foreach (var name in game.NotOpened)
            Console.WriteLine($"  {name}: NOT OPENED with this scheme");

        int bad = 0, checkedFiles = 0, dateFiles = 0, dateChecked = 0, decrypt2After = 0, decrypt2Before = 0;
        var problems = new List<string>();
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (archive, warc) in game.Archives)
        {
            int after = 0, before = 0, inArchive = 0, badHere = 0;
            foreach (var entry in warc.Entries)
            {
                if (!byName.TryGetValue(entry.Name, out var where))
                    byName[entry.Name] = where = new();
                where.Add(archive);
                uint flags = GameArchives.Flags(entry);
                if ((flags & 0x40000000) != 0)
                    after++;
                if ((flags & 0x20000000) != 0)
                    before++;
                bool check = FileChecks.Checked(entry.Name);
                bool usesDate = (flags & 0x40000000) != 0 && entry.IsPacked;
                if (!check && !usesDate)
                    continue;
                byte[] data;
                try
                {
                    data = OpenShiina.Archives.WarcOpener.OpenEntry(warc, entry);
                }
                catch (Exception e)
                {
                    problems.Add($"  {archive} {entry.Name}: {e.Message}");
                    badHere++;
                    continue;
                }
                bool hitsDate = usesDate && FileChecks.DateDwords(data).Count > 0;
                if (hitsDate)
                    dateFiles++;
                if (!check)
                    continue;
                inArchive++;
                if (hitsDate)
                    dateChecked++;
                if (FileChecks.Problem(entry.Name, data) is { } problem)
                {
                    problems.Add($"  {archive} {entry.Name}: {problem}{(hitsDate ? " (uses the date)" : "")}");
                    badHere++;
                }
            }
            Console.WriteLine($"  {archive}: {warc.Entries.Count} entries, Decrypt2 {after} after / {before} before unpacking, " +
                              $"{inArchive} checked, {badHere} not whole");
            decrypt2After += after;
            decrypt2Before += before;
            checkedFiles += inArchive;
            bad += badHere;
        }
        foreach (var line in problems.Take(40))
            Console.WriteLine(line);
        if (problems.Count > 40)
            Console.WriteLine($"  ... and {problems.Count - 40} more");

        Console.WriteLine($"{checkedFiles} files checked, {bad} not whole.");
        if (decrypt2After + decrypt2Before == 0)
            Console.WriteLine("No entry is flagged for Decrypt2: DecodeBin and its date do not matter.");
        else if (game.Scheme.DecodeBin == null)
            Console.WriteLine($"{decrypt2After + decrypt2Before} entries are flagged for Decrypt2 but the scheme has no DecodeBin: give it one (datefind).");
        else
            Console.WriteLine($"{dateFiles} files use DecodeBin's date ({dateChecked} of them checked above)" +
                              (bad > 0 ? ": if those are the ones not whole, run datefind." : ""));
        if (decrypt2Before > 0)
            Console.WriteLine($"{decrypt2Before} entries run Decrypt2 before unpacking: datefind does not read those.");

        var shared = byName.Where(p => p.Value.Count > 1 && !p.Key.StartsWith("DUMMY", StringComparison.OrdinalIgnoreCase))
                           .GroupBy(p => string.Join(" + ", p.Value)).ToList();
        if (shared.Count > 0)
        {
            Console.WriteLine("The same name in several archives (the first one the scripts mount wins):");
            foreach (var group in shared)
                Console.WriteLine($"  {group.Key}: {group.Count()} ({string.Join(", ", group.Take(4).Select(p => p.Key))}{(group.Count() > 4 ? ", ..." : "")})");
        }
        return bad == 0 && game.NotOpened.Count == 0 ? 0 : 2;
    }
}
