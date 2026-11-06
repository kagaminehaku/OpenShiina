// arcdump.nro: DumpGame for each game folder in sdmc:/OpenShiina/Games, written to
// sdmc:/OpenShiina/arcdump/<folder>.txt, with Formats.Json from the .nro's romfs. Started by
// nxlink, what it prints goes to the PC (nxlink -s) as well as to the screen.
#include <cstdio>
#include <filesystem>
#include <memory>

#include <switch.h>
#include <unistd.h>

#include "ArcDump.h"
#include "archives/FormatManager.h"
#include "platform/Files.h"

using namespace openshiina;

namespace {

constexpr const char* kGames = "sdmc:/OpenShiina/Games";
constexpr const char* kOut = "sdmc:/OpenShiina/arcdump";

void Show(const char* text)
{
    std::printf("%s", text);
    consoleUpdate(nullptr);
}

} // namespace

int main(int, char**)
{
    consoleInit(nullptr);
    padConfigureInput(1, HidNpadStyleSet_NpadStandard);
    PadState pad;
    padInitializeDefault(&pad);
    socketInitializeDefault();
    int nxlink = nxlinkStdio();
    romfsInit();

    auto manager = std::make_unique<archives::FormatManager>("romfs:/Formats.Json");
    for (const auto& error : manager->LoadErrors())
        std::printf("%s\n", error.c_str());
    archives::FormatManager::SetInstance(std::move(manager));

    std::filesystem::create_directories(kOut);
    auto folders = Files::FolderNames(kGames);
    if (folders.empty())
        std::printf("No game folders in %s\n", kGames);
    u64 started = armGetSystemTick();
    for (const auto& folder : folders)
    {
        std::printf("%s\n", folder.c_str());
        consoleUpdate(nullptr);
        std::string outPath = Files::Combine(kOut, folder + ".txt");
        std::FILE* out = Files::Open(outPath, "wb");
        if (!out)
        {
            std::printf("  cannot write %s\n", outPath.c_str());
            continue;
        }
        int bad = tools::DumpGame(Files::Combine(kGames, folder), out, [](const std::string& archive) {
            std::printf("  %s\n", archive.c_str());
            consoleUpdate(nullptr);
        });
        std::fclose(out);
        if (bad < 0)
            std::printf("  not opened\n");
        else
            std::printf("  %d files not decoded\n", bad);
    }
    std::printf("Done in %.1f s. Press + to exit.\n", double(armTicksToNs(armGetSystemTick() - started)) / 1e9);
    Show("");

    while (appletMainLoop())
    {
        padUpdate(&pad);
        if (padGetButtonsDown(&pad) & HidNpadButton_Plus)
            break;
        consoleUpdate(nullptr);
    }
    romfsExit();
    if (nxlink >= 0)
        close(nxlink);
    socketExit();
    consoleExit(nullptr);
    return 0;
}
