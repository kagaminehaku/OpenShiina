// A test run of the Switch player: a game played by a script, as scnrun plays it on the PC, with no
// one at the console (tools/devhost/devrun.ps1 sends the script, devhost's RUNF starts the player
// with "test:<name>"). The script is sdmc:/OpenShiina/Tests/<name>.txt, a setting a line:
//
//   game=<folder of Games>          the game (as the list names its folder)
//   frames=<n>                      how many frames, then the player leaves (back to devhost)
//   mouse=<frame:x,y[:buttons[:frames]];...>   scnrun's SCNRUN_MOUSE (more lines add to it)
//   press=<frame:key[:frames],...>             scnrun's SCNRUN_PRESS (keys as virtual keys)
//   every=<n>                       the game's picture saved every n frames, and at the end
//   clock=fixed | real              fixed (the default): 1/60 s a frame, as scnrun, so that the
//                                   pictures are those of the PC; real: the console's clock
//
// Lines starting with '#' are comments. The pictures go to sdmc:/OpenShiina/Tests/<name>/ as
// frame_NNNN.png (scnrun's names); the game's saves to its Saves folder there, emptied first, so that
// the run starts as scnrun's does, from no saves.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "scripting/ScnVm.h"

namespace openshiina::player {

class TestRun
{
public:
    /// The script sdmc:/OpenShiina/Tests/<name>.txt; nothing, with why in error, when it cannot be read.
    static std::optional<TestRun> Read(const std::string& root, const std::string& name, std::string& error);

    std::string Name, Game, Folder;
    uint64_t Frames = 0;
    int Every = 0;
    bool FixedClock = true;

    /// The keys held and where the mouse is at frame, and the messages it goes down or up with
    /// (as scnrun: the mouse's messages carry its place, the keys' a scan code of 0).
    bool KeyDown(int key, int frame) const;
    std::pair<int, int> MousePosition(int frame) const;
    int MouseButtons(int frame) const;
    void SendMessages(int frame, scripting::ScnVm& vm) const;

    /// The game's picture (BGR rows) as Folder/frame_NNNN.png.
    void SavePicture(scripting::ScnVm& vm, int frame) const;

private:
    struct Press
    {
        int From, To, Key;
    };
    struct Click
    {
        int From, X, Y, Buttons, To;
    };
    std::vector<Press> m_presses;
    std::vector<Click> m_mouse;
};

} // namespace openshiina::player
