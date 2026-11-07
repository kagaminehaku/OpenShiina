# OpenShiina native: the player's core in C++ for the Nintendo Switch (homebrew, libnx). The C#
# player stays the one for PC and Android; the PC build here is to compare with it.
#
#   PC (to compare with the C# player):   cmake -S native -B native/build -G Ninja && cmake --build native/build
#   Switch:                               native/build-switch.sh (devkitPro's Switch.cmake toolchain)
cmake_minimum_required(VERSION 3.20)
project(OpenShiinaNative C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release)
endif()

add_library(miniz STATIC third_party/miniz/miniz.c)
target_include_directories(miniz SYSTEM PUBLIC third_party/miniz)
# Only the mz_ names: the zlib ones would be inline functions each file leaves unused
target_compile_definitions(miniz PUBLIC MINIZ_NO_ZLIB_COMPATIBLE_NAMES)

add_library(openshiina_core STATIC
    src/archives/Decoder.cpp
    src/archives/EncryptionScheme.cpp
    src/archives/FormatManager.cpp
    src/archives/WarcOpener.cpp
    src/archives/WarcRangeDecoder.cpp
    src/game/GameData.cpp
    src/game/GameSetup.cpp
    src/io/ArcView.cpp
    src/io/Checksum.cpp
    src/platform/DibShapes.cpp
    src/platform/Files.cpp
    src/scripting/ScnAddressSpace.cpp
    src/scripting/ScnOpcodes.cpp
    src/scripting/ScnFonts.cpp
    src/scripting/ScnVm.cpp
    src/scripting/ScnVm.Blur32.cpp
    src/scripting/ScnVm.Calc.cpp
    src/scripting/ScnVm.Core.cpp
    src/scripting/ScnVm.Draw.cpp
    src/scripting/ScnVm.Effects.cpp
    src/scripting/ScnVm.Enlarge16.cpp
    src/scripting/ScnVm.Enlarge32.cpp
    src/scripting/ScnVm.Engine233.cpp
    src/scripting/ScnVm.Engine234.cpp
    src/scripting/ScnVm.Engine250.cpp
    src/scripting/ScnVm.Events.cpp
    src/scripting/ScnVm.Files.cpp
    src/scripting/ScnVm.Gdi.cpp
    src/scripting/ScnVm.Input.cpp
    src/scripting/ScnVm.Layers.cpp
    src/scripting/ScnVm.Menus.cpp
    src/scripting/ScnVm.Misc.cpp
    src/scripting/ScnVm.Movie.cpp
    src/scripting/ScnVm.Music.cpp
    src/scripting/ScnVm.Native.cpp
    src/scripting/ScnVm.NativeKernels.cpp
    src/scripting/ScnVm.NativeRoutines.cpp
    src/scripting/ScnVm.Paint.cpp
    src/scripting/ScnVm.Pending.cpp
    src/scripting/ScnVm.Pictures.cpp
    src/scripting/ScnVm.Printf.cpp
    src/scripting/ScnVm.RioSub.cpp
    src/scripting/ScnVm.Scale16.cpp
    src/scripting/ScnVm.Scale32.cpp
    src/scripting/ScnVm.Scale32Sse.cpp
    src/scripting/ScnVm.ScaleArea.cpp
    src/scripting/ScnVm.Sound.cpp
    src/scripting/ScnVm.Sprites.cpp
    src/scripting/ScnVm.Subpixel32.cpp
    src/scripting/ScnVm.Surfaces.cpp
    src/scripting/ScnVm.System.cpp
    src/scripting/ScnVm.Text.cpp
    src/scripting/ScnVm.Window.cpp
    src/scripting/X86Decoder.cpp
    src/scripting/X86Routine.cpp
    src/text/Text.cpp
    src/util/Encoding.cpp
)
target_include_directories(openshiina_core PUBLIC src)
target_include_directories(openshiina_core SYSTEM PUBLIC third_party/json)
target_link_libraries(openshiina_core PUBLIC miniz)

if(MSVC)
    # /fp:precise: no contractions of a * b + c, as in the C# player
    target_compile_options(openshiina_core PRIVATE /W4 /utf-8 /permissive- /fp:precise)
    target_compile_definitions(openshiina_core PRIVATE _CRT_SECURE_NO_WARNINGS)
else()
    # GCC fuses a * b + c by default on ARM: each operation must round as in the C# player. The
    # scripts' arithmetic wraps as C#'s int does (unchecked)
    target_compile_options(openshiina_core PRIVATE -Wall -Wextra -ffp-contract=off -fwrapv)
endif()

if(NINTENDO_SWITCH)
    # devhost.nro: runs the .nro files nxlink sends, and the programs it runs come back to it
    add_executable(devhost tools/devhost/main.cpp)
    target_link_libraries(devhost PRIVATE openshiina_core)
    target_compile_options(devhost PRIVATE -Wall -Wextra)
    nx_generate_nacp(devhost.nacp NAME "OpenShiina devhost" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(devhost NACP devhost.nacp)

    add_executable(arcdump tools/arcdump/ArcDump.cpp tools/arcdump/main_switch.cpp tools/devhost/DevHostReturn.cpp)
    target_include_directories(arcdump PRIVATE tools/devhost)
    target_link_libraries(arcdump PRIVATE openshiina_core)
    target_compile_options(arcdump PRIVATE -ffp-contract=off)
    # The romfs: Formats.Json from the C# player's data
    set(ARCDUMP_ROMFS "${CMAKE_CURRENT_BINARY_DIR}/arcdump-romfs")
    file(MAKE_DIRECTORY "${ARCDUMP_ROMFS}")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/Formats.Json" "${ARCDUMP_ROMFS}/Formats.Json" COPYONLY)
    nx_generate_nacp(arcdump.nacp NAME "OpenShiina arcdump" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(arcdump NACP arcdump.nacp ROMFS "${ARCDUMP_ROMFS}")

    add_executable(spacetest tools/spacetest/SpaceTest.cpp tools/spacetest/main_switch.cpp tools/devhost/DevHostReturn.cpp)
    target_include_directories(spacetest PRIVATE tools/devhost)
    target_link_libraries(spacetest PRIVATE openshiina_core)
    target_compile_options(spacetest PRIVATE -Wall -Wextra)
    nx_generate_nacp(spacetest.nacp NAME "OpenShiina spacetest" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(spacetest NACP spacetest.nacp)

    # OpenShiina.nro: the player (SDL2 from devkitPro's portlibs); the romfs holds the C# player's
    # Data: Formats.Json and the opcode tables
    find_package(PkgConfig REQUIRED)
    pkg_check_modules(SDL2 REQUIRED IMPORTED_TARGET sdl2)
    pkg_check_modules(FREETYPE REQUIRED IMPORTED_TARGET freetype2)
    add_executable(player player/main_switch.cpp player/FreeTypeFonts.cpp tools/devhost/DevHostReturn.cpp)
    target_include_directories(player PRIVATE tools/devhost)
    target_link_libraries(player PRIVATE openshiina_core PkgConfig::SDL2 PkgConfig::FREETYPE)
    target_compile_options(player PRIVATE -Wall -Wextra)
    set(PLAYER_ROMFS "${CMAKE_CURRENT_BINARY_DIR}/player-romfs")
    file(MAKE_DIRECTORY "${PLAYER_ROMFS}/Data")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/Formats.Json" "${PLAYER_ROMFS}/Data/Formats.Json" COPYONLY)
    file(COPY "${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/ScnOps" DESTINATION "${PLAYER_ROMFS}/Data")
    nx_generate_nacp(player.nacp NAME "OpenShiina" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(player NACP player.nacp ROMFS "${PLAYER_ROMFS}" OUTPUT OpenShiina.nro)
else()
    add_executable(arcdump tools/arcdump/ArcDump.cpp tools/arcdump/main.cpp)
    target_link_libraries(arcdump PRIVATE openshiina_core)

    add_executable(spacetest tools/spacetest/SpaceTest.cpp tools/spacetest/main.cpp)
    target_link_libraries(spacetest PRIVATE openshiina_core)

    # scnrun: a game's scripts on the C++ interpreter, to compare with the C# player's ScnBoot
    add_executable(scnrun tools/scnrun/main.cpp tools/scnrun/GdiFonts.cpp tools/scnrun/GdiShapes.cpp)
    target_link_libraries(scnrun PRIVATE openshiina_core)
    # ellipsecheck: DibShapes (the Switch's ellipses) against GDI, the C# GdiEllipseCheck's fixed cases
    add_executable(ellipsecheck tools/ellipsecheck/main.cpp tools/scnrun/GdiShapes.cpp)
    target_link_libraries(ellipsecheck PRIVATE openshiina_core)
    # routinecheck: the x86 routines' signatures against the C# player's (ScnTools routines)
    add_executable(routinecheck tools/routinecheck/main.cpp)
    target_link_libraries(routinecheck PRIVATE openshiina_core)
    if(MSVC)
        target_compile_options(scnrun PRIVATE /W4 /utf-8 /permissive-)
        target_compile_definitions(scnrun PRIVATE _CRT_SECURE_NO_WARNINGS)
        target_compile_options(ellipsecheck PRIVATE /W4 /utf-8 /permissive-)
        target_compile_options(routinecheck PRIVATE /W4 /utf-8 /permissive-)
    endif()
    if(MSVC)
        target_compile_options(spacetest PRIVATE /W4 /utf-8 /permissive-)
    endif()
endif()
