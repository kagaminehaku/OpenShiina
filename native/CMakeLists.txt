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
    src/io/ArcView.cpp
    src/io/Checksum.cpp
    src/platform/Files.cpp
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
    # GCC fuses a * b + c by default on ARM: each operation must round as in the C# player
    target_compile_options(openshiina_core PRIVATE -Wall -Wextra -ffp-contract=off)
endif()

if(NINTENDO_SWITCH)
    add_executable(arcdump tools/arcdump/ArcDump.cpp tools/arcdump/main_switch.cpp)
    target_link_libraries(arcdump PRIVATE openshiina_core)
    target_compile_options(arcdump PRIVATE -ffp-contract=off)
    # The romfs: Formats.Json from the C# player's data
    set(ARCDUMP_ROMFS "${CMAKE_CURRENT_BINARY_DIR}/arcdump-romfs")
    file(MAKE_DIRECTORY "${ARCDUMP_ROMFS}")
    configure_file("${CMAKE_CURRENT_SOURCE_DIR}/../src/OpenShiina.Core/Data/Formats.Json" "${ARCDUMP_ROMFS}/Formats.Json" COPYONLY)
    nx_generate_nacp(arcdump.nacp NAME "OpenShiina arcdump" AUTHOR "kagaminehaku" VERSION "0.1.0")
    nx_create_nro(arcdump NACP arcdump.nacp ROMFS "${ARCDUMP_ROMFS}")
else()
    add_executable(arcdump tools/arcdump/ArcDump.cpp tools/arcdump/main.cpp)
    target_link_libraries(arcdump PRIVATE openshiina_core)
endif()
