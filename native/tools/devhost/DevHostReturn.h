// A program devhost.nro started goes back to it when it exits: devhost passes its own path as the
// last argument, "_DEVHOST_=<path>", which ReturnAtExit takes off argv and gives hbloader as the
// next .nro to load. Called from userAppInit, which libnx runs after reading the arguments and
// before main; started from hbmenu, the program goes back to hbmenu as before.
#pragma once

namespace openshiina::devhost {

/// True when devhost started the program.
bool ReturnAtExit();

} // namespace openshiina::devhost
