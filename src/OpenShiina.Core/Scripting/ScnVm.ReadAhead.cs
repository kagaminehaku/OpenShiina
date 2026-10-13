// Reading ahead: the pictures and sounds a scenario names, read from the archives on pool threads
// a few files before the scripts ask for them. Decrypting and unpacking a picture is most of the
// time of 04B0 (10-50 ms for a background or a character's sheet on a PC, more on a phone), and
// the scripts load them one after another as a scene starts, which held the frame. Each scenario
// file (.TXT) the scripts read is searched for names of the archives' pictures and sounds, in the
// order they come (read by any opcode, or by the engine's own background loader 00CE); when the
// scripts read one of them, the next few are read ahead, so the reads
// run while the scene plays. A file read ahead is the same bytes the scripts would have read:
// only when it is ready changes. With LoadInBackground (the players; ScnBoot: SCNBOOT_BGLOAD=1).
// While the game skips, its voices are not played (its sound effects and music may still be): once
// three sounds of a folder read ahead were passed by unread ("v\" in Bitch Nee-chan), that folder's
// sounds are not read ahead until the scripts read one of them again (a phone's perf.log of
// skipping: 818 files read ahead, 80 of them used; ScnBoot holding Ctrl: 164 voices read ahead,
// none used, while its sound effects were).

using System.Text.RegularExpressions;

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    // How many of a scenario's next files are read ahead, and how many bytes may wait at most
    private const int AheadFiles = 6;
    private long AheadBytes => FileCacheBytes / 2;

    // Reads ahead not taken yet, by name, with the file's size (interpreter's thread only)
    private readonly Dictionary<string, (Task<byte[]?> Read, long Size)> m_ahead = new(StringComparer.OrdinalIgnoreCase);
    // The last scenario's files in the order they first come, and each one's place
    private string[] m_scenarioFiles = [];
    private readonly Dictionary<string, int> m_scenarioOrder = new(StringComparer.OrdinalIgnoreCase);

    // A picture or sound named in a scenario: "e\eva01.s25", "st\hm10_m.s25", "se01.ogg"
    private static readonly Regex s_mediaName = new(@"[0-9A-Za-z_\-]+(?:\\[0-9A-Za-z_\-]+)*\.(?:s25|ogg|wav)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether files are read ahead (with <see cref="LoadInBackground"/>; ScnBoot: SCNBOOT_AHEAD=0 turns it off).</summary>
    public bool ReadAhead { get; set; } = true;

    // By folder, the sounds read ahead and passed by unread since the scripts last read one (3:
    // the game skips them)
    private readonly Dictionary<string, int> m_soundsPassed = new(StringComparer.OrdinalIgnoreCase);
    // The scenario the files are of
    private string m_scenarioName = "";
    private static bool IsSound(string name) =>
        name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    private static string FolderOf(string name) => name.LastIndexOfAny(['\\', '/']) is >= 0 and int at ? name[..at] : "";

    private bool Skipped(string name) => IsSound(name) && m_soundsPassed.GetValueOrDefault(FolderOf(name)) >= 3;

    /// <summary>Files read ahead, and of those the scripts then read (ScnBoot's report).</summary>
    public (int Read, int Used) ReadAheadCounts => (m_aheadRead, m_aheadUsed);
    private int m_aheadRead, m_aheadUsed;

    /// <summary>The read of the file if it was read ahead (done or not), else null.</summary>
    private Task<byte[]?>? TakeAheadRead(string name)
    {
        if (!m_ahead.Remove(name, out var ahead))
            return null;
        m_aheadUsed++;
        return ahead.Read;
    }

    /// <summary>
    /// The file if it was read ahead (waiting for the read when it is not done yet), else null;
    /// <paramref name="how"/> says which, for perf.log.
    /// </summary>
    private byte[]? TakeAhead(string name, out string how)
    {
        how = "";
        if (TakeAheadRead(name) is not { } read)
            return null;
        how = read.IsCompleted ? ", read ahead" : ", read ahead, waited for";
        try
        {
            return read.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Read again on this thread: an error shows there as it would have
            return null;
        }
    }

    /// <summary>
    /// With perf.log: the reads of files that held the interpreter at least 30 ms ("name, KB,
    /// ms, how"), taken by PerfMeter every second.
    /// </summary>
    public List<string> SlowReads { get; } = new();

    private void NoteRead(string name, int bytes, long started, string how)
    {
        if (OpTimes == null)
            return;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms >= 30 && SlowReads.Count < 64)
            SlowReads.Add($"{name} {bytes >> 10} KB {ms:F0} ms{how}");
    }

    /// <summary>
    /// A scenario was read: its pictures and sounds are what comes next (the one being played,
    /// read again, goes on where it was).
    /// </summary>
    private void LearnScenario(string name, byte[] text)
    {
        // Its lines, to tell which one is shown (ScnVm.Lines.cs)
        NoteScenario(name, text);
        if (!LoadInBackground || !ReadAhead || name.Equals(m_scenarioName, StringComparison.OrdinalIgnoreCase))
            return;
        m_scenarioName = name;
        var files = new List<string>();
        m_scenarioOrder.Clear();
        foreach (Match m in s_mediaName.Matches(Encodings.cp932.GetString(text)))
        {
            string file = m.Value;
            if (m_scenarioOrder.ContainsKey(file) || m_host.ArchiveFileSize(file) is not { } size || size > FileCacheBytes / 2)
                continue;
            m_scenarioOrder[file] = files.Count;
            files.Add(file);
        }
        m_scenarioFiles = files.ToArray();
        // Reads ahead for the scenario before that one are let go
        foreach (string old in m_ahead.Keys.Where(k => !m_scenarioOrder.ContainsKey(k)).ToList())
            m_ahead.Remove(old);
        StartAhead(0);
    }

    /// <summary>The scripts read a file: if the scenario names it, the files after it are read ahead.</summary>
    private void ReadAheadAfter(string name)
    {
        if (!LoadInBackground || !ReadAhead || !m_scenarioOrder.TryGetValue(name, out int at))
            return;
        if (IsSound(name))
            m_soundsPassed.Remove(FolderOf(name));
        StartAhead(at + 1);
    }

    private void StartAhead(int from)
    {
        // Files passed by without being read (a branch not taken, a sound skipped) are let go
        foreach (var (name, _) in m_ahead.Where(a => m_scenarioOrder.GetValueOrDefault(a.Key, -1) < from - AheadFiles).ToList())
        {
            m_ahead.Remove(name);
            if (IsSound(name))
                m_soundsPassed[FolderOf(name)] = m_soundsPassed.GetValueOrDefault(FolderOf(name)) + 1;
        }
        long waiting = m_ahead.Values.Sum(a => a.Size);
        // The next AheadFiles files, but sounds of a folder the game skips
        int taken = 0;
        for (int k = from; taken < AheadFiles && k < Math.Min(from + 4 * AheadFiles, m_scenarioFiles.Length); k++)
        {
            string name = m_scenarioFiles[k];
            if (Skipped(name))
                continue;
            taken++;
            if (m_fileCache.ContainsKey(name) || m_ahead.ContainsKey(name) || m_host.ArchiveFileSize(name) is not { } size)
                continue;
            if (waiting + size > AheadBytes)
                break;
            waiting += size;
            m_aheadRead++;
            m_ahead[name] = (Task.Run(() => ReadArchiveFile(name)), size);
        }
    }
}
