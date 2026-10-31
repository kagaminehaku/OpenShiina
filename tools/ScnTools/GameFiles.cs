using System.IO.Enumeration;
using OpenShiina.Archives;

// ScnTools files <game folder> [pattern]: the entries of a game's archives (archive, name, size
// unpacked, WARC flags), those whose name matches a pattern (* and ?, any case) when one is given.
// ScnTools extract <game folder> <pattern> <out folder>: those files decoded, each as
// <archive stem>_<name> (the same name may be in several archives).
static class GameFiles
{
    public static int List(string[] args)
    {
        if (args.Length is not (1 or 2))
        {
            Console.WriteLine("usage: ScnTools files <game folder> [pattern]");
            return 1;
        }
        using var game = GameArchives.Open(args[0], out string? why);
        if (game == null)
        {
            Console.WriteLine($"{args[0]}: {why}");
            return 3;
        }
        string pattern = args.Length == 2 ? args[1] : "*";
        int count = 0;
        foreach (var (archive, _, entry) in game.Entries())
        {
            if (!FileSystemName.MatchesSimpleExpression(pattern, entry.Name))
                continue;
            Console.WriteLine($"{archive}\t{entry.Name}\t{entry.UnpackedSize}\t{GameArchives.Flags(entry):X8}");
            count++;
        }
        Console.Error.WriteLine($"{count} files");
        return 0;
    }

    public static int Extract(string[] args)
    {
        if (args.Length != 3)
        {
            Console.WriteLine("usage: ScnTools extract <game folder> <pattern> <out folder>");
            return 1;
        }
        using var game = GameArchives.Open(args[0], out string? why);
        if (game == null)
        {
            Console.WriteLine($"{args[0]}: {why}");
            return 3;
        }
        Directory.CreateDirectory(args[2]);
        int count = 0;
        foreach (var (archive, warc, entry) in game.Entries())
        {
            if (!FileSystemName.MatchesSimpleExpression(args[1], entry.Name))
                continue;
            string file = Path.Combine(args[2], Path.GetFileNameWithoutExtension(archive) + "_" + entry.Name.Replace('\\', '_').Replace('/', '_'));
            File.WriteAllBytes(file, WarcOpener.OpenEntry(warc, entry));
            Console.WriteLine(file);
            count++;
        }
        Console.Error.WriteLine($"{count} files written");
        return count > 0 ? 0 : 2;
    }
}
