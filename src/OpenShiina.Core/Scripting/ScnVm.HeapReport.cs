// The scripts' memory as a report: the blocks in use by size, and the free room (ScnBoot prints
// it when the memory runs out, to tell a scene that needs that much from blocks never given back
// or from room cut into pieces).

namespace OpenShiina.Scripting;

public sealed partial class ScnVm
{
    /// <summary>The blocks in use by size (the largest sizes first), the top of the heap, the free ranges and the largest.</summary>
    public string HeapReport()
    {
        var sizes = m_blocks.Values.GroupBy(s => s).OrderByDescending(g => g.Key).Take(12)
            .Select(g => $"{g.Key / 1048576.0:F1} MB x{g.Count()}");
        long free = m_freeRanges.Values.Sum(v => (long)v);
        int largest = m_freeRanges.Count > 0 ? m_freeRanges.Values.Max() : 0;
        return $"{m_blocks.Count} blocks, {HeapInUse >> 20} MB; largest: {string.Join(", ", sizes)}; " +
               $"top {(m_heapTop - HeapRegion) >> 20} of {(CodeRegion - HeapRegion) >> 20} MB, {m_freeRanges.Count} free ranges below it, " +
               $"{free >> 20} MB, the largest {largest / 1048576.0:F1} MB";
    }
}
