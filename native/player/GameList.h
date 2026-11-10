// The Switch player's list of games: the folders of sdmc:/OpenShiina/Games (and of the console's
// own storage, user:/OpenShiina/Games, when it is mounted), each one recognised once as the C#
// player's library does it (GameLibrary.Identify: the scheme its archives and .exe tell, its name,
// its .exe for the icon) and remembered in sdmc:/OpenShiina/library.tsv. A list made later only
// recognises the folders that are new or changed since (a folder's time), unless asked to look at
// them all again; a folder no scheme opens is listed under its own name, marked unknown. What the
// player keeps of each game besides (played.tsv: when it was played last, for how long, the scheme
// chosen for a game its .exe does not tell, as C# LibraryGame) goes by the folder's name, so a game
// moved between the SD card and the console keeps it.
#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace openshiina::archives {
struct EncryptionScheme;
}

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
    /// When it was played last (seconds since 1970, 0: never) and for how long in all.
    int64_t LastPlayed = 0, PlaySeconds = 0;
    /// The scheme chosen for it (its .exe does not tell), else empty.
    std::string Scheme;
};

/// What played.tsv keeps of a game.
struct GameRecord
{
    int64_t LastPlayed = 0, PlaySeconds = 0;
    std::string Scheme;
};

/// The records of played.tsv by folder name.
std::map<std::string, GameRecord> ReadRecords(const std::string& file);
/// The game in folder played now, for seconds more (0 when it starts).
void RecordPlay(const std::string& file, const std::string& folder, int64_t seconds);
/// The scheme chosen for the game in folder (empty: none).
void ChooseScheme(const std::string& file, const std::string& folder, const std::string& scheme);
/// The game in folder taken out of the records (deleted).
void ForgetGame(const std::string& file, const std::string& folder);

/// The games of the roots (internal: a root of the console's storage), the one played last first,
/// those no scheme opens at the end; cache: the file they are remembered in; records: played.tsv;
/// again: every folder recognised anew.
std::vector<GameEntry> ListGames(const std::vector<std::pair<std::string, bool>>& roots, const std::string& cache, const std::string& records,
                                 bool again);

/// The scheme to open the game in path with: the one its archives and .exe tell, else the one
/// chosen for it in records; null when neither.
std::shared_ptr<const archives::EncryptionScheme> SchemeFor(const std::string& path, const std::string& records);

/// A scheme that decoded every file tried (C# SchemeDetector.Candidate).
struct SchemeCandidate
{
    std::string Name;
    int Right = 0, Tried = 0;
};

/// The schemes that decode every file tried in the game's archives, best first (C# SchemeDetector:
/// S25 pictures, Ogg and WAVE files, whose first bytes are known once decoded).
std::vector<SchemeCandidate> DetectSchemes(const std::string& path);

} // namespace openshiina::player
