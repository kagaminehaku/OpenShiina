// Moving a game's folder between the SD card and the console's storage (see GameMove.h)
#include "GameMove.h"

#include <cerrno>
#include <cstdio>
#include <cstring>
#include <dirent.h>
#include <sys/stat.h>
#include <unistd.h>
#include <vector>

#include <switch.h>

#include "platform/Files.h"

namespace openshiina::player {

namespace {

constexpr int64_t kFat32Largest = (int64_t(1) << 32) - 1;

/// The names in a folder (not . and ..), or false when it cannot be read.
bool Entries(const std::string& folder, std::vector<std::string>& names)
{
    DIR* dir = opendir(folder.c_str());
    if (!dir)
        return false;
    while (dirent* item = readdir(dir))
    {
        std::string name = item->d_name;
        if (name != "." && name != "..")
            names.push_back(std::move(name));
    }
    closedir(dir);
    return true;
}

void Measure(const std::string& folder, FolderMeasure& measure)
{
    std::vector<std::string> names;
    if (!Entries(folder, names))
    {
        measure.Readable = false;
        return;
    }
    for (const auto& name : names)
    {
        std::string path = Files::Combine(folder, name);
        struct stat st;
        if (stat(path.c_str(), &st) != 0)
        {
            measure.Readable = false;
            continue;
        }
        if (S_ISDIR(st.st_mode))
            Measure(path, measure);
        else
        {
            measure.Bytes += int64_t(st.st_size);
            measure.Files++;
            if (int64_t(st.st_size) > kFat32Largest)
                measure.HugeFile = true;
        }
    }
}

struct Copier
{
    const std::function<bool(int64_t)>& Progress;
    std::string& Error;
    std::vector<uint8_t> Buffer = std::vector<uint8_t>(size_t(4) << 20);
    int64_t Done = 0;

    bool File(const std::string& from, const std::string& to)
    {
        std::FILE* in = std::fopen(from.c_str(), "rb");
        if (!in)
        {
            Error = "cannot read " + from + " (" + std::strerror(errno) + ")";
            return false;
        }
        std::FILE* out = std::fopen(to.c_str(), "wb");
        if (!out)
        {
            Error = "cannot write " + to + " (" + std::strerror(errno) + ")";
            std::fclose(in);
            return false;
        }
        bool ok = true;
        while (true)
        {
            size_t got = std::fread(Buffer.data(), 1, Buffer.size(), in);
            if (got == 0)
            {
                if (std::ferror(in))
                {
                    Error = "cannot read " + from;
                    ok = false;
                }
                break;
            }
            if (std::fwrite(Buffer.data(), 1, got, out) != got)
            {
                Error = "cannot write " + to + " (" + std::strerror(errno) + ")";
                ok = false;
                break;
            }
            Done += int64_t(got);
            if (!Progress(Done))
            {
                Error = "cancelled";
                ok = false;
                break;
            }
        }
        std::fclose(in);
        if (std::fclose(out) != 0 && ok)
        {
            Error = "cannot write " + to + " (" + std::strerror(errno) + ")";
            ok = false;
        }
        return ok;
    }

    bool Folder(const std::string& from, const std::string& to)
    {
        if (mkdir(to.c_str(), 0777) != 0)
        {
            Error = "cannot make " + to + " (" + std::strerror(errno) + ")";
            return false;
        }
        std::vector<std::string> names;
        if (!Entries(from, names))
        {
            Error = "cannot read " + from;
            return false;
        }
        for (const auto& name : names)
        {
            std::string source = Files::Combine(from, name), target = Files::Combine(to, name);
            bool ok = Files::IsDirectory(source) ? Folder(source, target) : File(source, target);
            if (!ok)
                return false;
        }
        return true;
    }
};

} // namespace

FolderMeasure MeasureFolder(const std::string& folder)
{
    FolderMeasure measure;
    Measure(folder, measure);
    return measure;
}

int64_t FreeSpace(const char* device)
{
    FsFileSystem* fs = fsdevGetDeviceFileSystem(device);
    s64 free = 0;
    if (!fs || R_FAILED(fsFsGetFreeSpace(fs, "/", &free)))
        return -1;
    return int64_t(free);
}

bool CopyFolder(const std::string& folder, const std::string& target, const std::function<bool(int64_t)>& progress, std::string& error)
{
    if (Files::IsDirectory(target) || Files::IsFile(target))
    {
        error = target + " is there already";
        return false;
    }
    Copier copier{progress, error};
    if (copier.Folder(folder, target))
        return true;
    RemoveFolder(target);
    return false;
}

bool RemoveFolder(const std::string& folder)
{
    std::vector<std::string> names;
    if (!Entries(folder, names))
        return !Files::IsDirectory(folder);
    bool all = true;
    for (const auto& name : names)
    {
        std::string path = Files::Combine(folder, name);
        if (Files::IsDirectory(path))
            all = RemoveFolder(path) && all;
        else
            all = unlink(path.c_str()) == 0 && all;
    }
    return rmdir(folder.c_str()) == 0 && all;
}

std::string ConsoleStorageKind()
{
    // Atmosphère's exosphere answers config item 65007 (ExosphereEmummcType): 0 on the sysMMC
    if (R_FAILED(splInitialize()))
        return {};
    u64 type = 0;
    Result rc = splGetConfig(SplConfigItem(65007), &type);
    splExit();
    if (R_FAILED(rc))
        return {};
    return type == 0 ? "sysMMC" : "emuMMC";
}

} // namespace openshiina::player
