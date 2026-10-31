using System.Buffers.Binary;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenShiina.Archives;

// ScnTools datefind <game folder> [--apply <Formats.Json>]: works out the date DecodeBin holds at
// 0x1020 for a game (engine-notes.md, "DecodeBin's date"). Decrypt2 XORs some dwords of
// 0x200-0x2FF with it, so with another date D those dwords change by (date ^ D): each candidate
// is tried on the files that use it and kept when they come out whole: Ogg pages with right CRCs,
// S25 index slots that are 0 or point at a frame inside the file, strict Shift-JIS. The
// candidates: every date from 2000 to 2030 written as hex digits (0x20161012), and what an S25
// index slot gives when its right value is 0. --apply writes the one found into Formats.Json as
// the scheme's own DecodeBin (the shared one with that date).
static class DateFind
{
    const int PerKind = 24;

    public static int Run(string[] args)
    {
        if (args.Length is not (1 or 3) || args.Length == 3 && args[1] != "--apply")
        {
            Console.WriteLine("usage: ScnTools datefind <game folder> [--apply <Formats.Json>]");
            return 1;
        }
        using var probe = GameArchives.Open(args[0], out string? why);
        if (probe == null)
        {
            Console.WriteLine($"{args[0]}: {why}");
            return 3;
        }
        var scheme = probe.Scheme;
        // Without a DecodeBin the scheme leaves Decrypt2 out: read with the shared one
        byte[] bin = scheme.DecodeBin ?? SharedDecodeBin() ?? throw new InvalidOperationException("no shared DecodeBin in Formats.Json");
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan(0x1020));
        var reading = scheme.DecodeBin != null ? scheme : CopyWith(scheme, bin);
        using var game = scheme.DecodeBin != null ? null : GameArchives.Open(args[0], out _, reading);
        var archives = (game ?? probe).Archives;
        Console.WriteLine($"{scheme.Name}: {(scheme.DecodeBin == null ? "no DecodeBin (reading with the shared one), " : "")}date now {used:X8}");

        // Files that use the date, by kind
        var samples = new Dictionary<string, List<(string Name, byte[] Data, List<int> Dwords)>>();
        foreach (var (archive, warc) in archives)
            foreach (var entry in warc.Entries)
            {
                if ((GameArchives.Flags(entry) & 0x40000000) == 0 || !entry.IsPacked || !FileChecks.Checked(entry.Name))
                    continue;
                string kind = Kind(entry.Name);
                if (samples.TryGetValue(kind, out var have) && have.Count >= PerKind)
                    continue;
                byte[] data = WarcOpener.OpenEntry(warc, entry);
                var dwords = FileChecks.DateDwords(data);
                if (dwords.Count == 0)
                    continue;
                if (!samples.TryGetValue(kind, out var list))
                    samples[kind] = list = new();
                list.Add((archive + " " + entry.Name, data, dwords));
            }
        if (samples.Count == 0)
        {
            Console.WriteLine("No S25, TXT or Ogg file uses the date: it cannot be told from the files, and does not matter for them.");
            return 0;
        }

        // The candidates
        var candidates = new HashSet<uint>();
        for (int y = 2000; y <= 2030; y++)
            for (int m = 1; m <= 12; m++)
                for (int d = 1; d <= DateTime.DaysInMonth(y, m); d++)
                    candidates.Add(Convert.ToUInt32($"{y:D4}{m:D2}{d:D2}", 16));
        foreach (var (_, data, dwords) in samples.GetValueOrDefault("S25") ?? [])
            foreach (int i in dwords)
                if (0x200 + 4 * i < 8 + 4 * BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)))
                    candidates.Add(used ^ BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x200 + 4 * i)));
        candidates.Add(used);

        var left = new HashSet<uint>(candidates);
        foreach (var (kind, list) in samples)
        {
            int narrowing = 0;
            foreach (var (name, data, dwords) in list)
            {
                var whole = left.Where(c => Whole(kind, data, dwords, used ^ c)).ToHashSet();
                if (whole.Count == left.Count)
                    continue;
                narrowing++;
                if (whole.Count == 0)
                {
                    Console.WriteLine($"  {name}: no candidate makes it whole (spoilt otherwise, or the date is none of these)");
                    continue;
                }
                left = whole;
            }
            Console.WriteLine($"  {list.Count} {kind} files use the date, {narrowing} narrowed it: {left.Count} candidates left");
        }

        if (left.Count == 1)
        {
            uint date = left.First();
            Console.WriteLine(date == used
                ? $"The date is {date:X8}, as the scheme has it."
                : $"The date is {date:X8} (the scheme has {used:X8}).");
            if (args.Length == 3 && date != used)
                return Apply(args[2], scheme.Name, bin, date);
            return date == used ? 0 : 2;
        }
        Console.WriteLine($"Not told apart: {string.Join(" ", left.Order().Take(12).Select(c => c.ToString("X8")))}{(left.Count > 12 ? " ..." : "")}");
        Console.WriteLine(left.Contains(used) ? $"The scheme's {used:X8} is one of them." : $"The scheme's {used:X8} is not one of them.");
        return 2;
    }

    static string Kind(string name) => Path.GetExtension(name).ToUpperInvariant() switch
    {
        ".S25" => "S25",
        ".TXT" => "TXT",
        _ => "Ogg",
    };

    /// <summary>The file with the dwords XORed by <paramref name="change"/>, checked where they are.</summary>
    static bool Whole(string kind, byte[] data, List<int> dwords, uint change)
    {
        // Only the part the changed dwords are in is copied and checked: there are thousands of
        // candidates, and a file can be megabytes
        int length = kind switch
        {
            "S25" => data.Length,
            "TXT" => CharStart(data, Math.Min(data.Length, 0x400)),
            _ => Math.Min(data.Length, 0x300 + 0x10000 + 0x200),   // the pages to past 0x2FF (a page is at most 64 KB)
        };
        byte[] copy = kind == "S25" ? data : data.AsSpan(0, length).ToArray();
        uint[] kept = dwords.Select(i => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x200 + 4 * i))).ToArray();
        foreach (int i in dwords)
        {
            var at = copy.AsSpan(0x200 + 4 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(at, BinaryPrimitives.ReadUInt32LittleEndian(at) ^ change);
        }
        try
        {
            return kind switch
            {
                "S25" => S25Slots(copy, dwords),
                "TXT" => FileChecks.Text(copy.AsSpan(CharStart(copy, 0x180), length - CharStart(copy, 0x180)).ToArray()) == null,
                _ => OggPages(copy, dwords),
            };
        }
        finally
        {
            if (kind == "S25")
                for (int k = 0; k < dwords.Count; k++)
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x200 + 4 * dwords[k]), kept[k]);
        }
    }

    /// <summary>The first character boundary of Shift-JIS text at or after <paramref name="at"/> (walked from the start).</summary>
    static int CharStart(byte[] d, int at)
    {
        int p = 0;
        while (p < at && p < d.Length)
            p += d[p] is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC ? 2 : 1;
        return Math.Min(p, d.Length);
    }

    /// <summary>The changed dwords that are index slots: 0, or a frame whose header and rows fit.</summary>
    static bool S25Slots(byte[] d, List<int> dwords)
    {
        long frames = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4));
        foreach (int i in dwords)
        {
            int slot = 0x200 + 4 * i;
            if (slot >= 8 + 4 * frames)
                continue;   // frame data: nothing to check it by
            long at = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(slot));
            if (at == 0)
                continue;
            if (at < 8 + 4 * frames || at + 0x14 > d.Length)
                return false;
            long width = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at));
            long height = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at + 4));
            if (width > 0x4000 || height > 0x4000 || at + 0x14 + 4 * height > d.Length)
                return false;
        }
        return true;
    }

    /// <summary>The Ogg pages that hold the changed dwords have right CRCs.</summary>
    static bool OggPages(byte[] d, List<int> dwords)
    {
        int at = d.AsSpan(0, Math.Min(d.Length, 256)).IndexOf("OggS"u8);
        if (at < 0)
            return false;
        while (at + 27 <= d.Length && at < 0x300 && d.AsSpan(at, 4).SequenceEqual("OggS"u8))
        {
            int segments = d[at + 26], size = 27 + segments;
            if (at + size > d.Length)
                return false;
            for (int k = 0; k < segments; k++)
                size += d[at + 27 + k];
            if (at + size > d.Length)
                return false;
            if (dwords.Any(i => 0x200 + 4 * i + 4 > at && 0x200 + 4 * i < at + size)
                && FileChecks.OggCrc(d, at, size) != BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 22)))
                return false;
            at += size;
        }
        return true;
    }

    static byte[]? SharedDecodeBin()
    {
        if (FormatManager.Instance.SchemeFilePath is not { } path)
            return null;
        var json = JsonNode.Parse(File.ReadAllText(path));
        return json?["SharedData"]?["DecodeBin"]?.GetValue<string>() is { } hex ? Convert.FromHexString(hex) : null;
    }

    static EncryptionScheme CopyWith(EncryptionScheme s, byte[] bin) => new()
    {
        Name = s.Name, OriginalTitle = s.OriginalTitle, Version = s.Version, EntryNameSize = s.EntryNameSize,
        CryptKey = s.CryptKey, HelperKey = s.HelperKey, Region = s.Region, DecodeBin = bin,
        ShiinaImage = s.ShiinaImage, ExtraCrypt = s.ExtraCrypt,
    };

    /// <summary>The scheme's "DecodeBin" in Formats.Json set to <paramref name="bin"/> with <paramref name="date"/> at 0x1020.</summary>
    static int Apply(string path, string schemeName, byte[] bin, uint date)
    {
        string text = File.ReadAllText(path);
        int name = text.IndexOf($"\"Name\": \"{schemeName}\"", StringComparison.Ordinal);
        if (name < 0)
        {
            Console.WriteLine($"{path}: no scheme named {schemeName}");
            return 3;
        }
        var field = new Regex("\"DecodeBin\": (null|\"[^\"]*\")").Match(text, name);
        int next = text.IndexOf("\"Name\": ", name + 8, StringComparison.Ordinal);
        if (!field.Success || next >= 0 && field.Index > next)
        {
            Console.WriteLine($"{path}: the scheme {schemeName} has no DecodeBin field");
            return 3;
        }
        var copy = (byte[])bin.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(0x1020), date);
        text = text[..field.Index] + $"\"DecodeBin\": \"{Convert.ToHexString(copy)}\"" + text[(field.Index + field.Length)..];
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..]));
        Console.WriteLine($"{path}: {schemeName} has its own DecodeBin now, date {date:X8}.");
        return 0;
    }
}
