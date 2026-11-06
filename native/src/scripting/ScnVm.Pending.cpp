// The parts of ScnVm the core calls whose C# files are not ported yet. Each does what the C# player
// does as long as the opcodes of its file are not there (no graph movie, no music, no task text,
// no disc, no "Play from here" can have started without them); each goes to its own
// ScnVm.<Part>.cpp when that file is ported.
#include "scripting/ScnVm.h"

#include "text/Text.h"

namespace openshiina::scripting {

// ---- ScnVm.Menus.cs ----

bool ScnVm::RunsInBackground()
{
    // RIO.INI's Background, read at start, then as 07E5 / 07E6 set it
    if (!m_background)
        m_background = IniInt("RIO.INI", RioSection(), "Background", 0) != 0;
    return *m_background;
}

void ScnVm::SetRunsInBackground(bool value)
{
    m_background = value;
    UpdatePause();
}

// ---- ScnVm.Misc.cs: no disc was found (0A30) ----

std::optional<int> ScnVm::VirtualDiscSize(ScnContext&, const ScnInstruction&, const std::string&)
{
    return std::nullopt;
}

// ---- ScnVm.Engine250.cs: no glyph picture of 009C ----

const ScnGlyph* ScnVm::Glyph250For(int, int) { return nullptr; }

// ---- ScnVm.Warp.cs: nothing to play from ----

void ScnVm::PlayFromModule(ScnContext&, const std::string&) {}

// ---- ScnVm.GraphMovie.cs: no graph movie ----

void ScnVm::PumpGraphMovies() {}

// ---- ScnVm.Music.cs: no music ----

void ScnVm::StepMusicFades() {}

} // namespace openshiina::scripting
