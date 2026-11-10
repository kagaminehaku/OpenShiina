// Moving a game's folder between the SD card and the console's own storage (the Switch player's
// "Move"): measured first (its bytes, its files, whether one is too large for the console's FAT32),
// copied file by file, and only then the old folder deleted; a copy that fails or is cancelled is
// deleted again, the game left where it was.
#pragma once

#include <cstdint>
#include <functional>
#include <string>

namespace openshiina::player {

struct FolderMeasure
{
    int64_t Bytes = 0;
    int Files = 0;
    /// A file of 4 GB or more (the console's storage is FAT32: it holds less).
    bool HugeFile = false;
    /// Everything in it could be read.
    bool Readable = true;
};

FolderMeasure MeasureFolder(const std::string& folder);

/// The free bytes of a mounted device ("sdmc", "user"), or -1 when it cannot be told.
int64_t FreeSpace(const char* device);

/// Copies folder to target (made here, not there before); progress(bytes copied so far) says whether
/// to go on. False with why in error, the copy so far deleted.
bool CopyFolder(const std::string& folder, const std::string& target, const std::function<bool(int64_t)>& progress, std::string& error);

/// The folder and everything in it deleted; false when something stayed.
bool RemoveFolder(const std::string& folder);

/// What the console's storage is: "sysMMC", "emuMMC" (as Atmosphère tells it), or empty when it cannot
/// be told.
std::string ConsoleStorageKind();

} // namespace openshiina::player
