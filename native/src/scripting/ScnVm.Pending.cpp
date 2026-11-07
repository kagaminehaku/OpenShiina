// The parts of ScnVm the core calls whose C# files are not ported yet. Each does what the C# player
// does as long as the opcodes of its file are not there (no graph movie, no task text,
// no disc, no "Play from here" can have started without them); each goes to its own
// ScnVm.<Part>.cpp when that file is ported.
#include "scripting/ScnVm.h"

#include "text/Text.h"

namespace openshiina::scripting {

// ---- ScnVm.Misc.cs: no disc was found (0A30) ----

std::optional<int> ScnVm::VirtualDiscSize(ScnContext&, const ScnInstruction&, const std::string&)
{
    return std::nullopt;
}

// ---- ScnVm.Warp.cs: nothing to play from ----

void ScnVm::PlayFromModule(ScnContext&, const std::string&) {}

// ---- ScnVm.GraphMovie.cs: no graph movie ----

void ScnVm::PumpGraphMovies() {}


} // namespace openshiina::scripting
