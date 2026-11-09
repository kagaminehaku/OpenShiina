param([Parameter(Mandatory)] [string]$Keys, [Parameter(Mandatory)] [string]$Work,
      [string]$Build = (Join-Path $PSScriptRoot '..\..\build-switch'),
      [string]$Version = '0.0.0', [string]$TitleId = '01ff004f70659000')
# OpenShiina.nsp: the player installed as a title of its own (see README.md), from a Switch build
# ($Build: player.elf and player-romfs): its code as the title's main, the romfs as the title's.
# The npdm, the NACP's changes and the logos are the OpenShiina Loader's (..\forwarder), with this
# title id; packed by hacBrewPack (v3.05) with the console's prod.keys ($Keys, never kept here).
# Needs devkitPro's tools and a MinGW gcc for hacBrewPack.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$forwarder = Join-Path $here '..\forwarder'
$tools = 'C:\msys64\opt\devkitpro\tools\bin'
$Build = (Resolve-Path $Build).Path
New-Item -ItemType Directory -Force $Work | Out-Null
Push-Location $Work
try {
    # (git's progress and the compiler's warnings come on stderr, which PowerShell 5 would stop at:
    # their exit codes tell instead)
    $ErrorActionPreference = 'Continue'
    if (-not (Test-Path hacBrewPack)) {
        git clone -q https://github.com/rlaphoenix/hacBrewPack.git 2>&1 | Out-Null
        git -C hacBrewPack -c advice.detachedHead=false checkout -q 745b16e 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "hacBrewPack could not be fetched" }
    }
    $unix = '/' + ($Work -replace ':', '' -replace '\\', '/')
    $env:MSYSTEM = 'UCRT64'
    & C:\msys64\usr\bin\bash.exe -lc "cd '$unix/hacBrewPack' && ([ -f config.mk ] || cp config.mk.template config.mk) && make" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "hacBrewPack did not build ($LASTEXITCODE)" }
    $ErrorActionPreference = 'Stop'

    foreach ($d in 'exefs', 'control', 'logo') {
        Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $d | Out-Null
    }
    & "$tools\elf2nso.exe" "$Build\player.elf" exefs\main
    $npdm = (Get-Content -Raw (Join-Path $forwarder 'npdm.json')) -replace '01ff004f70658000', $TitleId -replace '"OpenShiinaLoader"', '"OpenShiina"'
    [IO.File]::WriteAllText("$Work\npdm.json", $npdm)
    # (npdmtool tells of each optional field left out on stderr, which is no error)
    $ErrorActionPreference = 'Continue'
    & "$tools\npdmtool.exe" npdm.json exefs\main.npdm 2>$null
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw "npdmtool failed ($LASTEXITCODE)" }
    # the NACP: no startup account and no save data, as the Loader's; but no automatic video
    # capture (0x3035 = 0, the Loader's 2): with it the system takes 96 MB of the title's memory
    # after it starts, which hbloader leaves free and the player, heap of all the memory, does not:
    # am then aborts (2128-0007) and the console stops with a fatal error
    & "$tools\nacptool.exe" --create 'OpenShiina' 'kagaminehaku' $Version control\control.nacp --titleid=$TitleId
    $nacp = [IO.File]::ReadAllBytes("$Work\control\control.nacp")
    $nacp[0x3025] = 0
    for ($i = 0x3080; $i -lt 0x318A; $i++) { $nacp[$i] = 0 }
    $nacp[0x3034] = 0
    $nacp[0x3035] = 0
    [IO.File]::WriteAllBytes("$Work\control\control.nacp", $nacp)
    Copy-Item (Join-Path $here '..\..\player\icon.jpg') control\icon_AmericanEnglish.dat -Force
    Copy-Item (Join-Path $forwarder 'NintendoLogo.png'), (Join-Path $forwarder 'StartupMovie.gif') logo -Force

    $env:PATH = "C:\msys64\ucrt64\bin;$env:PATH"
    Remove-Item -Recurse -Force hacbrewpack_nsp, hacbrewpack_temp, hacbrewpack_nca -ErrorAction SilentlyContinue
    & hacBrewPack\hacbrewpack.exe -k $Keys --exefsdir exefs --controldir control --romfsdir "$Build\player-romfs" --logodir logo --titleid $TitleId --keygeneration 1
    Get-ChildItem hacbrewpack_nsp | Select-Object FullName, Length
}
finally { Pop-Location }
