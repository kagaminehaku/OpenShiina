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
//   loads=background | sync         background (the default): the background loader (00CE) and
//                                   the reads ahead on their threads, as the player plays; sync: the
//                                   files read when asked, as scnrun reads them, so that a scene
//                                   does not wait for the SD card a few frames more than on the PC
//                                   (for pictures to compare with scnrun's)
//   times=<from>:<to>               the time of the scripts, of each embedded routine and opcode
//                                   in those frames, told in the log ("[times]") when they end: the
//                                   same frames of the same script are the same scene each run, so
//                                   two runs (before and after a change) compare as they are
//   saves=game | <folder>           the game starts with these saves (the player's own of the game,
//                                   or a folder under sdmc:/OpenShiina) in place of none: a script
//                                   that loads one gets to a scene in a few hundred frames
//   fastzoom=1                      the setting "Fast zoom" as the player has it (else off: the
//                                   pictures to the byte, as scnrun's), to time it
//
// Lines starting with '#' are comments. The pictures go to sdmc:/OpenShiina/Tests/<name>/ as
// frame_NNNN.png (scnrun's names); the game's saves to its Saves folder there, emptied first, so that
// the run starts as scnrun's does, from no saves.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>
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
    /// loads=sync: no background loader nor reads ahead.
    bool SyncLoads = false;
    /// The frames timed (times=), -1 when none.
    int TimesFrom = -1, TimesTo = -1;
    /// saves=: "game" or a folder under the root; empty: none.
    std::string Saves;
    /// fastzoom=1: the setting "Fast zoom" kept.
    bool FastZoom = false;

    /// The timed frames' times: the embedded routines', the opcodes', the scripts' in all.
    struct Times
    {
        std::unordered_map<std::string, scripting::ScnVm::RoutineTime> Routines;
        std::unordered_map<int, scripting::ScnVm::RoutineTime> Ops;
        double ScriptsMs = 0;
        int Frames = 0;
    };
    /// The lines the log tells them in ("[times] ...").
    std::vector<std::string> Report(const Times& times) const;

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
