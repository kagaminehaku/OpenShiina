// arcdump: every file of a game's archives decoded, one line each (archive, name, size, flags and
// the SHA-256 of the bytes), to compare the C++ archives with the C# player's on each platform
// (ScnTools extract, tools/arccompare.ps1).
#pragma once

#include <cstdio>
#include <functional>
#include <string>

namespace openshiina::tools {

/// Writes the game folder's lines to out; progress is told what is being read. Returns the number
/// of files that could not be decoded, or -1 when the folder's archives cannot be opened.
int DumpGame(const std::string& folder, std::FILE* out, const std::function<void(const std::string&)>& progress);

} // namespace openshiina::tools
