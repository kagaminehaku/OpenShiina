// The schemes that open a game's archives, for a game whose .exe is not in GameMap (renamed, or a
// version GARbro-Mod does not list): every scheme with keys is tried on a few files of each
// archive whose first bytes are known once decoded (S25 pictures "S25\0", Ogg "OggS", RIFF WAVE).
// The index of an archive does not depend on a game's whole key (its ShiinaImage), so an archive
// that opens proves little: the files must come out right. Schemes that differ only in what those
// first bytes do not reach (the DecodeBin's date, for one) all pass: the player then asks.

using OpenShiina.Archives;
using OpenShiina.IO;

namespace OpenShiina.Game;

public static class SchemeDetector
{
    /// <summary>A scheme that decoded the files tried: its name, and how many came out right of how many.</summary>
    public readonly record struct Candidate(string Name, int Right, int Tried);

    // Files a scheme is tried on: per archive, the first few with a known start, in the smallest
    // archives first (their indexes are the quickest to decode) until FilesInAll were tried
    private const int FilesPerArchive = 3, FilesInAll = 6;

    /// <summary>
    /// The schemes that decode every file tried in <paramref name="folder"/>, best first (empty
    /// when none does, or the folder has no archives of keyed WARC versions).
    /// </summary>
    public static List<Candidate> Detect(string folder, CancellationToken cancel = default)
    {
        var paths = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".war", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => new FileInfo(f).Length).ToList()
            : [];
        var results = new List<Candidate>();
        var views = new List<ArcView>();
        try
        {
            foreach (var path in paths)
                views.Add(new ArcView(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var v in views)
                v.Dispose();
            return results;
        }
        try
        {
            foreach (var scheme in FormatManager.Instance.KnownSchemes)
            {
                cancel.ThrowIfCancellationRequested();
                if (Try(scheme, views) is { } candidate)
                    results.Add(candidate);
            }
        }
        finally
        {
            foreach (var v in views)
                v.Dispose();
        }
        return results.OrderByDescending(c => c.Tried).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The scheme as a candidate when every file tried came out right, else null.</summary>
    private static Candidate? Try(EncryptionScheme scheme, List<ArcView> views)
    {
        int right = 0, tried = 0;
        foreach (var view in views)
        {
            if (tried >= FilesInAll)
                break;
            WarcArchive? archive;
            try
            {
                archive = WarcOpener.TryOpen(view, scheme);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                archive = null;
            }
            if (archive == null)
                continue;
            int here = 0;
            foreach (var entry in archive.Entries)
            {
                if (KnownStart(entry.Name) is not { } start)
                    continue;
                tried++;
                try
                {
                    var bytes = WarcOpener.OpenEntry(archive, entry);
                    if (bytes.AsSpan().StartsWith(start))
                        right++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // a wrong key: the file does not unpack
                }
                if (++here >= FilesPerArchive)
                    break;
            }
            // a file that came out wrong: no need to go on
            if (right < tried)
                return null;
        }
        return tried > 0 && right == tried ? new Candidate(scheme.Name, right, tried) : null;
    }

    /// <summary>The first bytes a file of that name has once decoded, or null for one without such a start.</summary>
    private static byte[]? KnownStart(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".s25" => "S25\0"u8.ToArray(),
        ".ogg" => "OggS"u8.ToArray(),
        ".wav" => "RIFF"u8.ToArray(),
        _ => null,
    };
}
