// The parts of ScnVm the core calls whose C# files are not ported yet. Each does what the C# player
// does as long as the opcodes of its file are not there (no graph movie, no task text,
// no "Play from here" can have started without them); each goes to its own
// ScnVm.<Part>.cpp when that file is ported.
#include "scripting/ScnVm.h"

#include "text/Text.h"

namespace openshiina::scripting {

// ---- ScnVm.Warp.cs: nothing to play from ----

void ScnVm::PlayFromModule(ScnContext&, const std::string&) {}

// ---- ScnVm.GraphMovie.cs: no graph movie ----

void ScnVm::PumpGraphMovies() {}


} // namespace openshiina::scripting
