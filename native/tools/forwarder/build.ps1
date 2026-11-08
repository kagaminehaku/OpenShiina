param([Parameter(Mandatory)] [string]$Keys, [Parameter(Mandatory)] [string]$Work,
      [string]$TitleId = '01ff004f70658000')
# OpenShiina Loader: the title on the HOME menu that starts the player with the console's memory
# (see README.md). In $Work: nx-hbloader v2.4.5 (commit 82b9512) with hbloader.patch, built with
# npdm.json, then packed by hacBrewPack (v3.05) with the console's prod.keys ($Keys, never kept
# here). Needs devkitPro's MSYS2 (make) and a MinGW gcc for hacBrewPack.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
New-Item -ItemType Directory -Force $Work | Out-Null
Push-Location $Work
try {
    if (-not (Test-Path nx-hbloader)) {
        git clone https://github.com/switchbrew/nx-hbloader.git
        git -C nx-hbloader checkout 82b9512
        git -C nx-hbloader apply --ignore-whitespace (Join-Path $here 'hbloader.patch')
    }
    if (-not (Test-Path hacBrewPack)) {
        git clone https://github.com/rlaphoenix/hacBrewPack.git
        git -C hacBrewPack checkout 745b16e
    }
    Copy-Item (Join-Path $here 'npdm.json') nx-hbloader\forwarder.json -Force
    $unix = '/' + ($Work -replace ':', '' -replace '\\', '/')
    $env:MSYSTEM = 'MSYS'
    & C:\msys64\usr\bin\bash.exe -lc "cd '$unix/nx-hbloader' && make CONFIG_JSON=forwarder.json"
    $env:MSYSTEM = 'UCRT64'
    & C:\msys64\usr\bin\bash.exe -lc "cd '$unix/hacBrewPack' && ([ -f config.mk ] || cp config.mk.template config.mk) && make"

    foreach ($d in 'exefs', 'control', 'logo', 'romfs') { New-Item -ItemType Directory -Force $d | Out-Null }
    Copy-Item nx-hbloader\hbl.nso exefs\main -Force
    Copy-Item nx-hbloader\hbl.npdm exefs\main.npdm -Force
    # the NACP: no startup account and no save data (as forwarders have them)
    & C:\msys64\opt\devkitpro\tools\bin\nacptool.exe --create 'OpenShiina' 'OpenShiina' '1.0.0' control\control.nacp --titleid=$TitleId
    $nacp = [IO.File]::ReadAllBytes("$Work\control\control.nacp")
    $nacp[0x3025] = 0
    for ($i = 0x3080; $i -lt 0x318A; $i++) { $nacp[$i] = 0 }
    $nacp[0x3034] = 0
    $nacp[0x3035] = 2
    [IO.File]::WriteAllBytes("$Work\control\control.nacp", $nacp)
    Copy-Item (Join-Path $here 'icon.jpg') control\icon_AmericanEnglish.dat -Force
    Copy-Item (Join-Path $here 'NintendoLogo.png'), (Join-Path $here 'StartupMovie.gif') logo -Force
    Set-Content -Encoding ascii romfs\loader.txt 'OpenShiina Loader: starts sdmc:/switch/openshiina/OpenShiina.nro'

    $env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
    Remove-Item -Recurse -Force hacbrewpack_nsp, hacbrewpack_temp, hacbrewpack_nca -ErrorAction SilentlyContinue
    & hacBrewPack\hacbrewpack.exe -k $Keys --exefsdir exefs --controldir control --romfsdir romfs --logodir logo --titleid $TitleId --keygeneration 1
    Get-ChildItem hacbrewpack_nsp | Select-Object FullName, Length
}
finally { Pop-Location }
