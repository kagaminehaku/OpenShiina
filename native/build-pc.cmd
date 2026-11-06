@echo off
rem Builds the PC targets (arcdump.exe) with Visual Studio's C++ tools and its CMake / Ninja.
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (
    echo Visual Studio with the C++ tools was not found.
    exit /b 1
)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set "CM=%VS%\Common7\IDE\CommonExtensions\Microsoft\CMake"
cd /d "%~dp0"
"%CM%\CMake\bin\cmake.exe" -S . -B build -G Ninja -DCMAKE_MAKE_PROGRAM="%CM%\Ninja\ninja.exe" -DCMAKE_BUILD_TYPE=Release || exit /b 1
"%CM%\CMake\bin\cmake.exe" --build build
