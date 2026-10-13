// How much memory the decoded files of the archives may keep (ScnVm.FileCacheBytes): the scripts
// read the same pictures again and again (a character's sheet at every change of expression, an
// event picture at every variant of it), and a file left out is decrypted and unpacked again each
// time: Bitch Nee-chan's 22 MB e\eva01.s25 29 times in a minute of skipping on a phone, 160 ms
// each, as the cache was 64 MB and kept files of up to a quarter of it. A file may now take up to
// half of the cache. Auto sizes it by the device's memory.

using OpenShiina.Localization;

namespace OpenShiina.Game;

public enum FileCacheSize
{
    /// <summary>By the device's memory (<see cref="FileCachePresets.Auto"/>).</summary>
    Auto,
    Potato,
    Low,
    Medium,
    High,
    Max,
}

public static class FileCachePresets
{
    private const long MB = 1L << 20;

    /// <summary>The sizes as the settings screens show them, in the screens' language: name, what it does.</summary>
    public static IReadOnlyList<(FileCacheSize Size, string Label, string Help)> Choices =>
    [
        (FileCacheSize.Auto, Strings.CacheAuto, Strings.CacheAutoHelp),
        (FileCacheSize.Potato, Strings.CachePotato, Strings.CachePotatoHelp),
        (FileCacheSize.Low, Strings.CacheLow, Strings.CacheLowHelp),
        (FileCacheSize.Medium, Strings.CacheMedium, Strings.CacheMediumHelp),
        (FileCacheSize.High, Strings.CacheHigh, Strings.CacheHighHelp),
        (FileCacheSize.Max, Strings.CacheMax, Strings.CacheMaxHelp),
    ];

    /// <summary>The bytes a setting gives.</summary>
    public static long Bytes(FileCacheSize size) => size switch
    {
        FileCacheSize.Potato => 32 * MB,
        FileCacheSize.Low => 64 * MB,
        FileCacheSize.Medium => 128 * MB,
        FileCacheSize.High => 256 * MB,
        FileCacheSize.Max => 512 * MB,
        _ => Auto(),
    };

    /// <summary>
    /// A thirty-second of the device's memory, in steps of 16 MB, from 64 MB to 512 MB: 12 GB
    /// gives 384 MB, 4 GB 128 MB. 64 MB when the memory is not known.
    /// </summary>
    public static long Auto()
    {
        long total = TotalMemory();
        if (total <= 0)
            return 64 * MB;
        return Math.Clamp(total / 32 / (16 * MB) * (16 * MB), 64 * MB, 512 * MB);
    }

    /// <summary>The device's memory: what the GC was told it has, else Linux's (Android's) MemTotal; 0 when neither says.</summary>
    public static long TotalMemory()
    {
        long gc = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (gc > 0)
            return gc;
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)
                    && long.TryParse(line[9..].Trim().Split(' ')[0], out long kb))
                    return kb * 1024;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not Linux, or not readable
        }
        return 0;
    }
}
