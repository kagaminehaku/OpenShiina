#!/bin/bash
# build.sh <keys> <work> [build] [version] [title id]: OpenShiina.nsp as build.ps1 makes it, for
# Linux (the release workflow's devkitPro container): the player of a Switch build (<build>:
# player.elf and player-romfs, default native/build-switch) as a title of its own, its npdm, NACP
# changes and logos the OpenShiina Loader's (../forwarder), packed by hacBrewPack (v3.05) with
# the console's prod.keys (<keys>, never kept here) into <work>/hacbrewpack_nsp. Needs devkitPro's
# tools ($DEVKITPRO), git, make and a gcc for hacBrewPack.
set -e
keys=$(realpath "$1")
work=$2
here=$(cd "$(dirname "$0")" && pwd)
build=$(realpath "${3:-$here/../../build-switch}")
version=${4:-0.0.0}
titleid=${5:-01ff004f70659000}
forwarder=$here/../forwarder
tools=$DEVKITPRO/tools/bin
mkdir -p "$work"
cd "$work"

if [ ! -d hacBrewPack ]; then
    git clone -q https://github.com/rlaphoenix/hacBrewPack.git
    git -C hacBrewPack -c advice.detachedHead=false checkout -q 745b16e
fi
(cd hacBrewPack && { [ -f config.mk ] || cp config.mk.template config.mk; } && make -s)

rm -rf exefs control logo hacbrewpack_nsp hacbrewpack_temp hacbrewpack_nca
mkdir -p exefs control logo
"$tools/elf2nso" "$build/player.elf" exefs/main
sed -e "s/01ff004f70658000/$titleid/g" -e 's/"OpenShiinaLoader"/"OpenShiina"/' "$forwarder/npdm.json" > npdm.json
# (npdmtool tells of each optional field left out on stderr, which is no error)
"$tools/npdmtool" npdm.json exefs/main.npdm 2>/dev/null
# The NACP: no startup account and no save data, as the Loader's; but no automatic video capture
# (0x3035 = 0, the Loader's 2): with it the system takes 96 MB of the title's memory after it
# starts, which the player, heap of all the memory, does not leave free (build.ps1)
"$tools/nacptool" --create 'OpenShiina' 'kagaminehaku' "$version" control/control.nacp --titleid=$titleid
nacp=control/control.nacp
printf '\0' | dd of=$nacp bs=1 seek=$((0x3025)) conv=notrunc status=none
dd if=/dev/zero of=$nacp bs=1 seek=$((0x3080)) count=$((0x318A - 0x3080)) conv=notrunc status=none
printf '\0\0' | dd of=$nacp bs=1 seek=$((0x3034)) conv=notrunc status=none
cp "$here/../../player/icon.jpg" control/icon_AmericanEnglish.dat
cp "$forwarder/NintendoLogo.png" "$forwarder/StartupMovie.gif" logo/

hacBrewPack/hacbrewpack -k "$keys" --exefsdir exefs --controldir control --romfsdir "$build/player-romfs" \
    --logodir logo --titleid $titleid --keygeneration 1
ls -l hacbrewpack_nsp
