// On the Switch the folders are read with opendir / readdir / stat: libstdc++'s std::filesystem
// finds nothing on sdmc:
#ifndef _WIN32
#define _POSIX_C_SOURCE 200809L
#endif

#include "platform/Files.h"

#include <algorithm>
#include <cerrno>
#include <cstring>
#include <system_error>

#ifndef _WIN32
#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>
#endif

namespace fs = std::filesystem;

namespace openshiina::Files {

fs::path ToPath(const std::string& utf8)
{
    return fs::path(std::u8string(utf8.begin(), utf8.end()));
}

std::string ToUtf8(const fs::path& path)
{
    auto u8 = path.u8string();
    return std::string(u8.begin(), u8.end());
}

std::FILE* Open(const std::string& path, const char* mode)
{
#ifdef _WIN32
    std::wstring wmode(mode, mode + std::char_traits<char>::length(mode));
    return _wfopen(ToPath(path).c_str(), wmode.c_str());
#else
    return std::fopen(path.c_str(), mode);
#endif
}

#ifdef _WIN32

bool IsFile(const std::string& path)
{
    std::error_code ec;
    return fs::is_regular_file(ToPath(path), ec);
}

bool IsDirectory(const std::string& path)
{
    std::error_code ec;
    return fs::is_directory(ToPath(path), ec);
}

bool CreateDirectories(const std::string& path)
{
    std::error_code ec;
    fs::create_directories(ToPath(path), ec);
    return IsDirectory(path);
}

static std::vector<std::string> Names(const std::string& folder, bool folders, std::string* error)
{
    std::vector<std::string> names;
    std::error_code ec;
    fs::directory_iterator it(ToPath(folder), ec), end;
    for (; !ec && it != end; it.increment(ec))
    {
        std::error_code kind;
        if (folders ? it->is_directory(kind) : it->is_regular_file(kind))
            names.push_back(ToUtf8(it->path().filename()));
    }
    if (ec && error)
        *error = ec.message();
    return names;
}

#else

bool IsFile(const std::string& path)
{
    struct stat st;
    return stat(path.c_str(), &st) == 0 && S_ISREG(st.st_mode);
}

bool IsDirectory(const std::string& path)
{
    struct stat st;
    return stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
}

bool CreateDirectories(const std::string& path)
{
    if (path.empty() || IsDirectory(path))
        return true;
    std::string parent = DirectoryName(path);
    // "sdmc:" and "/" are where the parents end
    if (!parent.empty() && parent.back() != ':' && !CreateDirectories(parent))
        return false;
    return mkdir(path.c_str(), 0777) == 0 || IsDirectory(path);
}

static std::vector<std::string> Names(const std::string& folder, bool folders, std::string* error)
{
    std::vector<std::string> names;
    DIR* dir = opendir(folder.c_str());
    if (!dir)
    {
        if (error)
            *error = std::strerror(errno);
        return names;
    }
    while (dirent* item = readdir(dir))
    {
        std::string name = item->d_name;
        if (name == "." || name == "..")
            continue;
        std::string path = Combine(folder, name);
        if (folders ? IsDirectory(path) : IsFile(path))
            names.push_back(std::move(name));
    }
    closedir(dir);
    return names;
}

#endif

std::string Combine(const std::string& folder, const std::string& name)
{
    if (folder.empty())
        return name;
    char last = folder.back();
    return last == '/' || last == '\\' ? folder + name : folder + "/" + name;
}

std::string FileName(const std::string& path)
{
    size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

std::string DirectoryName(const std::string& path)
{
    size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

std::string FileNameWithoutExtension(const std::string& path)
{
    std::string name = FileName(path);
    size_t dot = name.find_last_of('.');
    return dot == std::string::npos ? name : name.substr(0, dot);
}

std::string Extension(const std::string& path)
{
    std::string name = FileName(path);
    size_t dot = name.find_last_of('.');
    return dot == std::string::npos ? std::string() : name.substr(dot);
}

std::vector<std::string> FileNames(const std::string& folder, std::string* error) { return Names(folder, false, error); }
std::vector<std::string> FolderNames(const std::string& folder, std::string* error) { return Names(folder, true, error); }

bool ReadAll(const std::string& path, std::vector<uint8_t>& data)
{
    std::FILE* file = Open(path, "rb");
    if (!file)
        return false;
    data.clear();
    // The size first, so the bytes are read into one block in large pieces (a movie of 188 MB read
    // 64 KB at a time into a growing vector took seconds on the Switch); a file that grows on goes on
    // the same way
#ifdef _WIN32
    int64_t size = _fseeki64(file, 0, SEEK_END) == 0 ? _ftelli64(file) : -1;
    _fseeki64(file, 0, SEEK_SET);
#else
    int64_t size = fseeko(file, 0, SEEK_END) == 0 ? int64_t(ftello(file)) : -1;
    fseeko(file, 0, SEEK_SET);
#endif
    constexpr size_t Piece = 4 << 20;
    size_t have = 0, got;
    if (size > 0)
    {
        data.resize(size_t(size));
        while (have < data.size() && (got = std::fread(data.data() + have, 1, std::min(Piece, data.size() - have), file)) > 0)
            have += got;
        data.resize(have);
    }
    uint8_t buffer[65536];
    while ((got = std::fread(buffer, 1, sizeof buffer, file)) > 0)
        data.insert(data.end(), buffer, buffer + got);
    bool ok = !std::ferror(file);
    std::fclose(file);
    return ok;
}

} // namespace openshiina::Files
