// C# Game/GameSetup.cs: what every front end does to start a game: read RIO.INI (the engine version,
// the first script, the window size), make the interpreter for that version with the platform's
// host, load the first script into slot 0 as the engine does, and keep save data in the player's own
// folder. (The C# player's settings - GPU, x86 translation, file cache presets - are its own.)
#pragma once

#include <memory>
#include <string>

#include "game/GameData.h"
#include "scripting/ScnVm.h"

namespace openshiina::game {

class GameSetup
{
public:
    std::string Version;
    /// The game: its scheme's name.
    std::string Game;
    std::string StartScript;
    int Width = 800, Height = 600;
    /// Where this game's save data goes: <saves root>/<game> (made when missing).
    std::string SaveFolder;

    /// Reads the game's RIO.INI; throws InvalidDataError when it is missing or names no engine version.
    static GameSetup Read(GameData& data, const std::string& savesRoot);

    /// The interpreter for this engine version (the opcode tables in dataFolder/ScnOps) with the
    /// first script loaded and started; throws InvalidDataError when the script is missing.
    std::unique_ptr<scripting::ScnVm> CreateVm(scripting::IScnHost& host, const std::string& dataFolder) const;

    /// PlayerFolders.FolderName: the characters a file name cannot hold become '_'.
    static std::string FolderName(const std::string& game);
};

} // namespace openshiina::game
