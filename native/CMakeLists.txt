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
    src/formats/AsfAudio.cpp
    src/formats/AsfFile.cpp
    src/formats/ExeIcon.cpp
    src/formats/Mdct.cpp
    src/formats/MovieAhead.cpp
    src/formats/MpegAudio.cpp
    src/formats/MpegVideo.cpp
    src/formats/Vc1Video.Blocks.cpp
    src/formats/Vc1Video.cpp
    src/formats/Vc1Video.Filters.cpp
    src/formats/Vc1Video.Motion.cpp
    src/formats/VlcTable.cpp
    src/formats/WmaAudio.cpp
    src/formats/WmaProAudio.cpp
    src/game/GameData.cpp
    src/game/GameSetup.cpp
    src/game/TranslationTables.cpp
    src/io/ArcView.cpp
    src/io/Checksum.cpp
    src/platform/DibShapes.cpp
    src/platform/Files.cpp
    src/scripting/ParallelRows.cpp
    src/scripting/ScnAddressSpace.cpp
    src/scripting/ScnOpcodes.cpp
    src/scripting/ScnFonts.cpp
    src/scripting/ScnGaiji.cpp
    src/scripting/ScnVm.cpp
    src/scripting/ScnVm.Accelerator.cpp
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
    src/scripting/ScnVm.GraphMovie.cpp
    src/scripting/ScnVm.Movie.cpp
    src/scripting/ScnVm.Music.cpp
    src/scripting/ScnVm.Native.cpp
    src/scripting/ScnVm.NativeKernels.cpp
    src/scripting/ScnVm.NativeRoutines.cpp
    src/scripting/ScnVm.Paint.cpp
    src/scripting/ScnVm.Pending.cpp
    src/scripting/ScnVm.Pictures.cpp
    src/scripting/ScnVm.Printf.cpp
    src/scripting/ScnVm.ReadAhead.cpp
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
    src/text/Unicode.cpp
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
    # The pixel routines' loops vectorised (NEON on the Switch): devkitPro's Release is -O2
    if(CMAKE_BUILD_TYPE STREQUAL "Release")
        target_compile_options(openshiina_core PRIVATE -O3)
    endif()
endif()

# The sounds of the scripts (C# Audio, Formats/AudioDecoder): the mixer, the sound buffers and the
# music streams; Ogg Vorbis with stb_vorbis (public domain), MPEG audio with minimp3 (CC0), both
# built as they come
add_library(audio_codecs STATIC third_party/stb_vorbis/stb_vorbis.c src/audio/minimp3.c)
target_include_directories(audio_codecs SYSTEM PRIVATE third_party)
target_compile_definitions(audio_codecs PRIVATE STB_VORBIS_NO_STDIO)
if(MSVC)
    target_compile_options(audio_codecs PRIVATE /w)
else()
    target_compile_options(audio_codecs PRIVATE -w -O3)
endif()
add_library(openshiina_audio STATIC
    src/audio/AudioDecoder.cpp
    src/audio/ScnMixer.cpp
    src/audio/ScnMusic.cpp
    src/audio/ScnSound.cpp
    src/audio/WdlResampler.cpp
)
target_include_directories(openshiina_audio SYSTEM PRIVATE third_party)
target_link_libraries(openshiina_audio PUBLIC openshiina_core PRIVATE audio_codecs)
if(MSVC)
    target_compile_options(openshiina_audio PRIVATE /W4 /utf-8 /permissive-)
else()
    target_compile_options(openshiina_audio PRIVATE -Wall -Wextra -O3)
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
    # The player draws with deko3d (player/ui); SDL is only its sound out
    add_executable(player player/main_switch.cpp player/FreeTypeFonts.cpp player/GameList.cpp player/GameMove.cpp player/Settings.cpp player/ui/DekoAccelerator.cpp player/ui/Renderer.cpp player/ui/UiText.cpp
                          tools/devhost/DevHostReturn.cpp)
    target_include_directories(player PRIVATE player tools/devhost)
    target_link_libraries(player PRIVATE openshiina_core openshiina_audio deko3d PkgConfig::SDL2 PkgConfig::FREETYPE)
    target_compile_options(player PRIVATE -Wall -Wextra -O3)
    set(PLAYER_ROMFS "${CMAKE_CURRENT_BINARY_DIR}/player-romfs")
    file(MAKE_DIRECTORY "${PLAYER_ROMFS}/Data" "${PLAYER_ROMFS}/shaders")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/Formats.Json" "${PLAYER_ROMFS}/Data/Formats.Json" COPYONLY)
    file(COPY "${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/ScnOps" DESTINATION "${PLAYER_ROMFS}/Data")
    file(COPY "${CMAKE_CURRENT_SOURCE_DIR}/player/romfs/ui" DESTINATION "${PLAYER_ROMFS}")
    # The shaders, compiled for the Tegra X1 when the player is built (uam), into the romfs
    set(PLAYER_SHADERS)
    foreach(shader ui_vsh:vert ui_fsh:frag picture_fsh:frag easu_fsh:frag rcas_fsh:frag)
        string(REPLACE ":" ";" parts "${shader}")
        list(GET parts 0 name)
        list(GET parts 1 stage)
        set(output "${PLAYER_ROMFS}/shaders/${name}.dksh")
        add_custom_command(OUTPUT "${output}"
            COMMAND "$ENV{DEVKITPRO}/tools/bin/uam" -s ${stage} -o "${output}" "${CMAKE_CURRENT_SOURCE_DIR}/player/shaders/${name}.glsl"
            DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/player/shaders/${name}.glsl"
            COMMENT "uam ${name}.glsl")
        list(APPEND PLAYER_SHADERS "${output}")
    endforeach()
    # The GPU mode's compute shaders are the C# player's (src/OpenShiina.Gpu/Shaders, Vulkan GLSL): its
    # push constants become a uniform block (every parameter a 4-byte scalar, the same layout), and
    # "packed", a word mesa's GLSL keeps for itself, another name
    foreach(name subpixel32 rotatezoom scale enlarge16)
        set(source "${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Gpu/Shaders/${name}.comp")
        set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${source}")
        file(READ "${source}" text)
        string(REPLACE "layout(push_constant) uniform" "layout(std140, binding = 0) uniform" text "${text}")
        string(REGEX REPLACE "([^A-Za-z_])packed\\(" "\\1packedLanes(" text "${text}")
        set(glsl "${CMAKE_CURRENT_BINARY_DIR}/gpu/${name}.glsl")
        file(WRITE "${glsl}" "${text}")
        set(output "${PLAYER_ROMFS}/shaders/${name}.dksh")
        add_custom_command(OUTPUT "${output}"
            COMMAND "$ENV{DEVKITPRO}/tools/bin/uam" -s comp -o "${output}" "${glsl}"
            DEPENDS "${glsl}"
            COMMENT "uam ${name}.comp")
        list(APPEND PLAYER_SHADERS "${output}")
    endforeach()
    add_custom_target(player_shaders DEPENDS ${PLAYER_SHADERS})
    add_dependencies(player player_shaders)
    nx_generate_nacp(player.nacp NAME "OpenShiina" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(player NACP player.nacp ICON "${CMAKE_CURRENT_SOURCE_DIR}/player/icon.jpg" ROMFS "${PLAYER_ROMFS}" OUTPUT OpenShiina.nro)
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
    # moviecheck: each frame of an MPEG-1 or Windows Media movie as the C# player's decoders make it (SHA-256s)
    add_executable(moviecheck tools/moviecheck/main.cpp)
    target_link_libraries(moviecheck PRIVATE openshiina_core)
    # gaijicheck: the translation code on test data, as the C# player's makes it
    add_executable(gaijicheck tools/gaijicheck/main.cpp)
    target_link_libraries(gaijicheck PRIVATE openshiina_core)
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
