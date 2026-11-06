#include "platform/Files.h"

#include <system_error>

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

static std::vector<std::string> Names(const std::string& folder, bool folders)
{
    std::vector<std::string> names;
    std::error_code ec;
    for (fs::directory_iterator it(ToPath(folder), ec), end; !ec && it != end; it.increment(ec))
    {
        std::error_code kind;
        if (folders ? it->is_directory(kind) : it->is_regular_file(kind))
            names.push_back(ToUtf8(it->path().filename()));
    }
    return names;
}

std::vector<std::string> FileNames(const std::string& folder) { return Names(folder, false); }
std::vector<std::string> FolderNames(const std::string& folder) { return Names(folder, true); }

bool ReadAll(const std::string& path, std::vector<uint8_t>& data)
{
    std::FILE* file = Open(path, "rb");
    if (!file)
        return false;
    data.clear();
    uint8_t buffer[65536];
    size_t got;
    while ((got = std::fread(buffer, 1, sizeof buffer, file)) > 0)
        data.insert(data.end(), buffer, buffer + got);
    bool ok = !std::ferror(file);
    std::fclose(file);
    return ok;
}

} // namespace openshiina::Files
