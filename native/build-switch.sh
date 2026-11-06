#!/bin/bash
# Builds the Switch targets (arcdump.nro) with devkitPro: run in MSYS2 (C:\msys64\usr\bin\bash.exe -l).
set -e
cd "$(dirname "$0")"
cmake -S . -B build-switch -G Ninja -DCMAKE_TOOLCHAIN_FILE="$DEVKITPRO/cmake/Switch.cmake" -DCMAKE_BUILD_TYPE=Release
cmake --build build-switch
ls -la build-switch/*.nro
