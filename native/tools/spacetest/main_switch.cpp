// spacetest.nro: the results on the screen and, started by nxlink -s, on the PC. Started by
// devhost.nro, it goes back to it when done; from hbmenu, + exits.
#include <cstdio>
#include <string>

#include <switch.h>
#include <unistd.h>

#include "DevHostReturn.h"
#include "SpaceTest.h"

namespace {

int s_nxlink = -1;
bool s_fromDevHost = false;

} // namespace

extern "C" void userAppInit()
{
    s_fromDevHost = openshiina::devhost::ReturnAtExit();
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
    openshiina::tools::RunSpaceTest([](const std::string& line) {
        std::printf("%s\n", line.c_str());
        if (s_nxlink >= 0)
            std::fprintf(stderr, "%s\n", line.c_str());
        consoleUpdate(nullptr);
    });
    if (!s_fromDevHost)
    {
        std::printf("Press + to exit.\n");
        padConfigureInput(1, HidNpadStyleSet_NpadStandard);
        PadState pad;
        padInitializeDefault(&pad);
        while (appletMainLoop())
        {
            padUpdate(&pad);
            if (padGetButtonsDown(&pad) & HidNpadButton_Plus)
                break;
            consoleUpdate(nullptr);
        }
    }
    consoleExit(nullptr);
    return 0;
}
