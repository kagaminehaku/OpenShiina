// Files and folders by UTF-8 paths on every platform (Windows takes them as UTF-16 underneath),
// and the case-insensitive look-ups the games need: their files are named as on Windows.
#pragma once

#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

namespace openshiina::Files {

std::filesystem::path ToPath(const std::string& utf8);
std::string ToUtf8(const std::filesystem::path& path);

/// fopen of a UTF-8 path; null when it cannot be opened.
std::FILE* Open(const std::string& path, const char* mode);

bool IsFile(const std::string& path);
bool IsDirectory(const std::string& path);
std::string Combine(const std::string& folder, const std::string& name);
std::string FileName(const std::string& path);
std::string DirectoryName(const std::string& path);
/// The name without its last extension (".war" of "a.b.war" goes, "a.b" stays).
std::string FileNameWithoutExtension(const std::string& path);
std::string Extension(const std::string& path);

/// The names (not paths) of a folder's files, or of its folders; none when it cannot be read.
std::vector<std::string> FileNames(const std::string& folder);
std::vector<std::string> FolderNames(const std::string& folder);

/// The whole file, or false when it cannot be read.
bool ReadAll(const std::string& path, std::vector<uint8_t>& data);

} // namespace openshiina::Files
