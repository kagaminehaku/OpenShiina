#include "DevHostReturn.h"

#include <cstring>
#include <string>

#include <switch.h>

// libnx's argc and argv, which crt0 passes to main after userAppInit
extern "C" int __system_argc;
extern "C" char** __system_argv;

namespace openshiina::devhost {

bool ReturnAtExit()
{
    constexpr char kMarker[] = "_DEVHOST_=";
    constexpr size_t kMarkerLength = sizeof kMarker - 1;
    if (__system_argc < 2 || std::strncmp(__system_argv[__system_argc - 1], kMarker, kMarkerLength) != 0)
        return false;
    std::string path = __system_argv[__system_argc - 1] + kMarkerLength;
    __system_argc--;
    __system_argv[__system_argc] = nullptr;
    // Awake while it runs, as devhost is: nobody touches the Switch between builds
    appletSetMediaPlaybackState(true);
    return envHasNextLoad() && R_SUCCEEDED(envSetNextLoad(path.c_str(), ("\"" + path + "\"").c_str()));
}

} // namespace openshiina::devhost
