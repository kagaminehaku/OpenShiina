// How the pixel routines share their rows between cores. Waking pool threads and handing out many
// small bands cost more processor than they save: Re: Rem Plus's held zoom (enlarge32 in bands
// of 16 rows) took 18 ms of processor a frame for 2.6 ms of wall time. So a call is cut into a
// few equal pieces of at least PieceWork pixels each, at most MaxParts, and work too small for
// two pieces runs on the calling thread. Measured on that zoom (enlarge32 on vectors, 1280 x 720
// a frame, i7-10750H): one thread 2.3 ms of processor and of wall time, two or three pieces
// 2.4 ms for 1.3 ms, four 3.3 ms for 1.5 ms, twelve 4.1 ms for 0.9 ms.

namespace OpenShiina.Scripting;

public static class ParallelRows
{
    /// <summary>The pixels a piece is worth at least (about half a millisecond of a pixel routine on a PC).</summary>
    public const long PieceWork = 1 << 18;

    /// <summary>
    /// The most pieces a call is cut into: four (and no more than the processors), or
    /// OPENSHIINA_THREADS (1: every routine on the interpreter's thread, for measuring).
    /// </summary>
    public static int MaxParts { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("OPENSHIINA_THREADS"), out int n) && n > 0 ? n : Math.Min(4, Environment.ProcessorCount);

    /// <summary>The pieces <paramref name="count"/> rows of <paramref name="work"/> pixels in all are cut into.</summary>
    public static int Parts(int count, long work) => (int)Math.Clamp(Math.Min(work / PieceWork, count), 1, Math.Max(1, MaxParts));

    /// <summary>
    /// Runs <paramref name="range"/>(from, to) over rows 0 to <paramref name="count"/>, in equal
    /// pieces on several threads when the work is worth it, else at once on this thread.
    /// </summary>
    public static void For(int count, long work, Action<int, int> range)
    {
        if (count <= 0)
            return;
        int parts = Parts(count, work);
        if (parts == 1)
        {
            range(0, count);
            return;
        }
        Parallel.For(0, parts, i => range((int)((long)count * i / parts), (int)((long)count * (i + 1) / parts)));
    }
}
