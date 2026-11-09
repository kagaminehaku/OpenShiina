// The Switch player's list of games: the folders of sdmc:/OpenShiina/Games (and of the console's
// own storage, user:/OpenShiina/Games, when it is mounted), each one recognised once as the C#
// player's library does it (GameLibrary.Identify: the scheme its archives and .exe tell, its name,
// its .exe for the icon) and remembered in sdmc:/OpenShiina/library.tsv. A list made later only
// recognises the folders that are new or changed since (a folder's time), unless asked to look at
// them all again; a folder no scheme opens is listed under its own name, marked unknown.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace openshiina::player {

struct GameEntry
{
    /// The game's folder.
    std::string Path;
    /// Its name: its scheme's, else the folder's.
    std::string Name;
    /// Its .exe, for the icon (empty: none known).
    std::string Exe;
    /// A scheme opens it.
    bool Known = false;
    /// On the console's own storage.
    bool Internal = false;
    /// The folder's time when it was recognised.
    int64_t Stamp = 0;
};

/// The games of the roots (internal: a root of the console's storage), by name; cache: the file
/// they are remembered in; again: every folder recognised anew.
std::vector<GameEntry> ListGames(const std::vector<std::pair<std::string, bool>>& roots, const std::string& cache, bool again);

} // namespace openshiina::player
