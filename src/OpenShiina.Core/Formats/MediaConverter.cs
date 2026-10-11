// Turns decrypted ShiinaRio media into standard files on extraction, as GARbro does by default:
// S25 / MI4 -> PNG (an S25 a file per frame, or the pictures the scripts compose of its layers),
// OGV -> OGG, PAD -> WAV. From GrandCrossExtractor.

namespace OpenShiina.Formats;

public static class MediaConverter
{
    /// <summary>
    /// The files to write for an entry. Formats that are not recognised, or fail to decode, come
    /// back unchanged under their own name. With <paramref name="scripts"/>, a layered S25 (event
    /// CG, standing sprite) the scenario scripts show is saved as the complete pictures the game
    /// shows instead of its frames (<see cref="S25Composer"/>). The files are made one at a time:
    /// write each before asking for the next.
    /// </summary>
    public static IEnumerable<(string FileName, byte[] Data)> Convert(string entryName, byte[] data, S25ScriptIndex? scripts = null)
    {
        string baseName = Path.GetFileNameWithoutExtension(entryName);
        if (S25Decoder.IsS25(data))
        {
            var frames = S25Decoder.DecodeAllFrames(data);
            var layout = scripts != null ? S25Layout.Analyze(data) : null;
            var combinations = layout != null ? scripts!.GetCombinations(entryName, layout) : null;
            if (combinations is { Count: > 0 })
                return S25Composer.Compose(baseName, frames, layout!, combinations).Select(c => (c.Name, PngEncoder.Encode(c.Image)));
            if (frames.Count == 1)
                return [(baseName + ".png", PngEncoder.Encode(frames[0].Image))];
            if (frames.Count > 1)
                return frames.Select(f => ($"{baseName}@{f.Index:D4}.png", PngEncoder.Encode(f.Image)));
        }
        else if (Mi4Decoder.IsMi4(data))
        {
            if (Mi4Decoder.Decode(data) is { } picture)
                return [(baseName + ".png", PngEncoder.Encode(picture))];
        }
        else if (AudioDecoder.IsOgv(data))
        {
            if (AudioDecoder.DecodeOgv(data) is { } ogg)
                return [(baseName + ".ogg", ogg)];
        }
        else if (AudioDecoder.IsPad(data))
        {
            if (AudioDecoder.DecodePad(data) is { } wav)
                return [(baseName + ".wav", wav)];
        }
        return [(entryName, data)];
    }

    private static readonly HashSet<string> s_reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name from an archive as a plain file name: entry names come from the archive itself, so
    /// one such as "..\..\x.exe" or "C:\x.bat" must not choose where a file is written.
    /// </summary>
    public static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar));
        foreach (char c in Path.GetInvalidFileNameChars().Append('\\').Append('/').Append(':'))
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            name = "unnamed";
        if (s_reserved.Contains(Path.GetFileNameWithoutExtension(name)))
            name = "_" + name;
        return name;
    }

    /// <summary>The full path of a file to write, always inside <paramref name="directory"/>.</summary>
    public static string SafeOutputPath(string directory, string name)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, SafeFileName(name)));
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing to write '{name}' outside the destination folder.");
        return path;
    }
}
