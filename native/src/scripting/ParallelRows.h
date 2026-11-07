// C# Scripting/ParallelRows.cs: how the pixel routines share their rows between cores. Waking pool
// threads and handing out many small bands cost more processor than they save, so a call is cut
// into a few equal pieces of at least PieceWork pixels each, at most MaxParts, and work too small
// for two pieces runs on the calling thread.
//
// The C# player hands the pieces to .NET's thread pool; here a pool of its own runs them, its
// threads waiting between calls. On the Switch they are kept on cores 1 and 2 (the interpreter's
// thread is on core 0), and a piece is worth less work: its cores are several times slower than a
// PC's, so smaller zooms gain from it too.
#pragma once

#include <cstdint>
#include <functional>

namespace openshiina::scripting::ParallelRows {

/// The pixels a piece is worth at least.
#ifdef __SWITCH__
constexpr int64_t PieceWork = int64_t(1) << 16;
#else
constexpr int64_t PieceWork = int64_t(1) << 18;
#endif

/// The most pieces a call is cut into: four (three on the Switch, its cores for programs), no more
/// than the processors, or OPENSHIINA_THREADS (1: every routine on the interpreter's thread).
int MaxParts();

/// The pieces count rows of work pixels in all are cut into.
int Parts(int count, int64_t work);

/// Runs range(from, to) over rows 0 to count, in equal pieces on several threads when the work is
/// worth it, else at once on this thread.
void For(int count, int64_t work, const std::function<void(int, int)>& range);

} // namespace openshiina::scripting::ParallelRows
