// The parts of ScnVm the core calls whose C# files are not ported yet. Each does what the C# player
// does as long as the opcodes of its file are not there (no graph movie, no music, no task text,
// no disc, no "Play from here" can have started without them); each goes to its own
// ScnVm.<Part>.cpp when that file is ported.
#include "scripting/ScnVm.h"

#include "text/Text.h"

namespace openshiina::scripting {

// ---- ScnVm.Input.cs ----

std::string ScnVm::RioSection()
{
    // The first section of RIO.INI ("[椎名里緒 v2.47]"), where the engine's own settings are
    openshiina::Bytes bytes;
    if (!m_host.ReadLooseFile("RIO.INI", bytes))
        return "";
    std::string text(bytes.begin(), bytes.end());
    size_t start = 0;
    while (start <= text.size())
    {
        size_t end = text.find('\n', start);
        std::string t = SjisTrim(text.substr(start, end == std::string::npos ? std::string::npos : end - start));
        if (!t.empty() && t.front() == '[' && t.back() == ']')
            return t.substr(1, t.size() - 2);
        if (end == std::string::npos)
            break;
        start = end + 1;
    }
    return "";
}

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

// ---- ScnVm.Text.cs: no task has text to draw (0083) ----

void ScnVm::StepTaskText(ScnContext& c)
{
    throw Error(c, "Task text (0083) is not ported yet");
}

// ---- ScnVm.Warp.cs: nothing to play from ----

void ScnVm::PlayFromModule(ScnContext&, const std::string&) {}

// ---- ScnVm.GraphMovie.cs: no graph movie ----

void ScnVm::PumpGraphMovies() {}

// ---- ScnVm.Music.cs: no music ----

void ScnVm::StepMusicFades() {}

} // namespace openshiina::scripting
