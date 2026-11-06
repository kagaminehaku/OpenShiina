// arcdump.nro: DumpGame for each game folder in sdmc:/OpenShiina/Games, written to
// sdmc:/OpenShiina/arcdump/<folder>.txt, with Formats.Json from the .nro's romfs. What it prints
// goes to the screen (stdout), to sdmc:/OpenShiina/arcdump/log.txt and, started by nxlink -s, to
// the PC (stderr). Started by devhost.nro, it goes back to it when done, without waiting for +.
//
// The network and nxlink are set up in userAppInit and closed in userAppExit, which libnx runs
// after the C library has closed stdout and stderr: closed in main, stderr would still name the
// socket when the C library closes it at exit, and the Switch stops on a fatal error there.
#include <cerrno>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <memory>
#include <string>

#include <dirent.h>
#include <switch.h>
#include <sys/stat.h>
#include <unistd.h>

#include "ArcDump.h"
#include "DevHostReturn.h"
#include "archives/FormatManager.h"
#include "platform/Files.h"
#include "util/Encoding.h"

using namespace openshiina;

namespace {

constexpr const char* kGames = "sdmc:/OpenShiina/Games";
constexpr const char* kOut = "sdmc:/OpenShiina/arcdump";

std::FILE* s_log = nullptr;
int s_nxlink = -1;
bool s_fromDevHost = false;

void Log(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    std::vprintf(format, args);
    va_end(args);
    if (s_nxlink >= 0)
    {
        va_start(args, format);
        std::vfprintf(stderr, format, args);
        va_end(args);
    }
    if (s_log)
    {
        va_start(args, format);
        std::vfprintf(s_log, format, args);
        va_end(args);
        std::fflush(s_log);
    }
    consoleUpdate(nullptr);
}

/// What a folder holds as the Switch sees it: each name, a file's size or <dir>, or why it cannot
/// be read; for when no game folder is found.
void ListFolder(const char* folder)
{
    Log("%s:\n", folder);
    DIR* dir = opendir(folder);
    if (!dir)
    {
        Log("  cannot open: %s\n", std::strerror(errno));
        return;
    }
    for (;;)
    {
        errno = 0;
        dirent* item = readdir(dir);
        if (!item)
        {
            if (errno != 0)
                Log("  readdir stopped: %s\n", std::strerror(errno));
            break;
        }
        std::string path = Files::Combine(folder, item->d_name);
        struct stat st;
        if (stat(path.c_str(), &st) != 0)
            Log("  %s  stat: %s\n", item->d_name, std::strerror(errno));
        else if (S_ISDIR(st.st_mode))
            Log("  %s  <dir>\n", item->d_name);
        else
            Log("  %s  %lld\n", item->d_name, (long long)st.st_size);
    }
    closedir(dir);
}

} // namespace

extern "C" void userAppInit()
{
    s_fromDevHost = devhost::ReturnAtExit();
    if (R_SUCCEEDED(socketInitializeDefault()))
        s_nxlink = nxlinkConnectToHost(false, true);
}

extern "C" void userAppExit()
{
    if (s_nxlink >= 0)
        close(s_nxlink);
    socketExit();
}

int main(int, char**)
{
    consoleInit(nullptr);
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    PadState pad;
    padInitializeDefault(&pad);
    romfsInit();

    bool outMade = Files::CreateDirectories(kOut);
    s_log = Files::Open(Files::Combine(kOut, "log.txt"), "wb");
    if (!outMade)
        Log("Cannot make %s: %s\n", kOut, std::strerror(errno));
    Log("nxlink %s\n", s_nxlink >= 0 ? "connected" : "not connected (only the screen and log.txt)");

    auto manager = std::make_unique<archives::FormatManager>("romfs:/Formats.Json");
    for (const auto& error : manager->LoadErrors())
        Log("%s\n", error.c_str());
    Log("Formats.Json: %zu schemes\n", manager->KnownSchemes().size());
    archives::FormatManager::SetInstance(std::move(manager));

    std::string error;
    auto folders = Files::FolderNames(kGames, &error);
    if (folders.empty())
    {
        Log("No game folders in %s%s%s\n", kGames, error.empty() ? "" : ": ", error.c_str());
        ListFolder("sdmc:/");
        ListFolder("sdmc:/OpenShiina");
    }
    ListFolder(kGames);
    // A folder with a Japanese name opened by its path, which listing did not find
    ListFolder("sdmc:/OpenShiina/Games/\xE3\x82\xA2\xE3\x82\xBA\xE3\x83\x97\xE3\x83\xA9\xE3\x82\xB9");
    u64 started = armGetSystemTick();
    for (const auto& folder : folders)
    {
        Log("%s\n", folder.c_str());
        std::string outPath = Files::Combine(kOut, folder + ".txt");
        std::FILE* out = Files::Open(outPath, "wb");
        if (!out)
        {
            Log("  cannot write %s\n", outPath.c_str());
            continue;
        }
        int bad = tools::DumpGame(Files::Combine(kGames, folder), out, [](const std::string& archive) {
            Log("  %s\n", archive.c_str());
        });
        std::fclose(out);
        if (bad < 0)
            Log("  not opened\n");
        else
            Log("  %d files not decoded\n", bad);
        // The SHA-256 of the whole dump: the same as that of the PC's arcdump file for the game
        // when every file comes out the same
        Bytes dump;
        if (Files::ReadAll(outPath, dump))
            Log("  dump %zu bytes, SHA-256 %s\n", dump.size(), Encoding::Sha256::HexOf(dump.data(), dump.size()).c_str());
    }
    // Started by devhost, it goes back there at once, for the next build
    Log("Done in %.1f s.%s\n", double(armTicksToNs(armGetSystemTick() - started)) / 1e9,
        s_fromDevHost ? " Back to devhost." : " Press + to exit.");

    while (!s_fromDevHost && appletMainLoop())
    {
        padUpdate(&pad);
        if (padGetButtonsDown(&pad) & HidNpadButton_Plus)
            break;
        consoleUpdate(nullptr);
    }
    if (s_log)
        std::fclose(s_log);
    romfsExit();
    consoleExit(nullptr);
    return 0;
}
