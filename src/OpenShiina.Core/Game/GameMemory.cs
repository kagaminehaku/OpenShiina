// What a game leaves when it closes. The scripts' memory is the system's and goes back at once
// (ScnVm.Dispose), but the managed part (the file cache, pictures and sounds read, a movie's
// streams: large objects, collected only with the oldest generation) waits for a collection that
// the home screen, allocating next to nothing, never brings on: each game played again added its
// own (Ao no Juuai: about 85 MB a time, its opening movie and file cache). So the players collect
// everything once a game has closed.

using System.Runtime;

namespace OpenShiina.Game;

public static class GameMemory
{
    /// <summary>
    /// A full collection, the large objects compacted, on a pool thread (the window goes on); call
    /// it once a game has closed and nothing holds it.
    /// </summary>
    public static void CollectAfterGame() => Task.Run(() =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    });
}
