param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$Out = '',
    [string[]]$Like = @(),
    [string]$LikeFile = '',
    [int]$BootFrames = 6000,
    [switch]$NoSweep
)
# Tries a game OpenShiina has not played yet, every step of docs/adding-a-game.md that runs on its
# own, and writes $Out\report.md: what passed, what did not, and the next step for each finding.
#
#   .\tests\newgame.ps1 -Folder "D:\Games\Some Game" [-Out newgame-somegame] [-LikeFile known-games.txt]
#
# Steps: the game's settings and executables; ScnTools arccheck (its archives with its scheme), and
# datefind when files that use DecodeBin's date are not whole; ScnTools opcheck; ScnTools routines
# (with -Like / -LikeFile: the folders of games that play, to match routines without a C# version
# by their shape); ScnBoot from the start for $BootFrames frames; tests\sweep.ps1 (not with
# -NoSweep), and the routines the sweep ran without a C# version. Build ScnTools and ScnBoot first
# (Release). Nothing is written to the game folder.

$root = Split-Path $PSScriptRoot -Parent
$scntools = Join-Path $root 'tools\ScnTools\bin\Release\net10.0\ScnTools.exe'
$scnboot = Join-Path $PSScriptRoot 'OpenShiina.ScnBoot\bin\Release\net10.0\scnboot.exe'
foreach ($exe in $scntools, $scnboot) {
    if (-not (Test-Path -LiteralPath $exe)) { throw "Build it first (Release): $exe" }
}
if (-not (Test-Path -LiteralPath $Folder)) { throw "No folder $Folder" }
if (-not $Out) { $Out = 'newgame-' + (Split-Path -Leaf $Folder) }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if ($LikeFile) { $Like += Get-Content -LiteralPath $LikeFile -Encoding UTF8 | Where-Object { $_ -and -not $_.StartsWith('#') } }
$Like = @($Like | Where-Object { $_ -and (Test-Path -LiteralPath $_) -and ((Resolve-Path -LiteralPath $_).Path -ne (Resolve-Path -LiteralPath $Folder).Path) })

$report = [Collections.Generic.List[string]]::new()
$findings = [Collections.Generic.List[string]]::new()
function Step($title) { Write-Host "== $title"; $report.Add(''); $report.Add("## $title"); $report.Add('') }
function Note($text) { Write-Host "   $text"; $report.Add($text) }
function Finding($text) { $findings.Add($text); Note "**To do:** $text" }
function Run($name, [string[]]$arguments) {
    $file = Join-Path $Out "$name.txt"
    # PowerShell 5.1 writes redirected output as UTF-16: lines are written as UTF-8 here
    & $scntools @arguments 2>&1 | ForEach-Object { "$_" } | Set-Content -LiteralPath $file -Encoding UTF8
    $code = $LASTEXITCODE
    return @{ Code = $code; Lines = @(Get-Content -LiteralPath $file -Encoding UTF8); File = "$name.txt" }
}

# 1. The game
Step 'The game'
$ini = Get-ChildItem -LiteralPath $Folder -Filter '*.ini' | Where-Object { (Get-Content -LiteralPath $_.FullName -TotalCount 1 -Encoding Default) -match 'v\d+\.\d+' } | Select-Object -First 1
$version = if ($ini) { [regex]::Match((Get-Content -LiteralPath $ini.FullName -TotalCount 1 -Encoding Default), 'v(\d+\.\d+)').Groups[1].Value } else { '' }
Note "Folder: $Folder"
Note ("Executables: " + ((Get-ChildItem -LiteralPath $Folder -Filter '*.exe' | ForEach-Object Name) -join ', '))
Note ("Archives: " + ((Get-ChildItem -LiteralPath $Folder -Filter '*.war' | ForEach-Object Name) -join ', '))
Note ("Engine: " + $(if ($version) { "v$version ($($ini.Name))" } else { 'no engine version found in an .INI' }))
if (-not $version) { Finding 'No engine version: read the game''s RIO.INI (or the .INI of its .exe) - adding-a-game.md, "The game".' }

# 2. Archives
Step 'Archives (ScnTools arccheck)'
$arc = Run 'arccheck' @('arccheck', $Folder)
$arc.Lines | Select-Object -First 3 | ForEach-Object { Note $_ }
$arc.Lines | Where-Object { $_ -match 'files checked|use DecodeBin|flagged for Decrypt2|before unpacking:|NOT OPENED' } | ForEach-Object { Note $_ }
$scheme = ''
if ($arc.Code -eq 3) {
    Finding "The archives do not open: $($arc.Lines -join ' ') - adding-a-game.md, ""No scheme""."
} else {
    $scheme = ($arc.Lines[0] -split ' \(scheme from')[0]
    if ($arc.Lines[0] -match 'scheme from detected') { Finding 'The scheme was found by trying the known ones, not by the .exe name: add the .exe to GameMap - adding-a-game.md, "GameMap".' }
    if ($arc.Code -ne 0) {
        if ($arc.Lines -match 'uses the date') {
            Step 'DecodeBin date (ScnTools datefind)'
            $date = Run 'datefind' @('datefind', $Folder)
            $date.Lines | Select-Object -Last 3 | ForEach-Object { Note $_ }
            if ($date.Lines -match 'The date is ([0-9A-F]{8}) \(the scheme has') {
                Finding "DecodeBin's date is $($Matches[1]): ScnTools datefind ""$Folder"" --apply src\OpenShiina.Core\Data\Formats.Json, build, run again - adding-a-game.md, ""DecodeBin's date""."
            } else {
                Finding 'Files are not whole and the date was not told: adding-a-game.md, "DecodeBin''s date".'
            }
        } else {
            Finding "Files are not whole ($($arc.File)), not from the date: the scheme's other keys - adding-a-game.md, ""Files not whole""."
        }
    }
    if ($arc.Lines -match 'same name in several archives') { Note 'The same name is in several archives: see arccheck.txt (the first archive mounted wins).' }
}

if ($arc.Code -ne 3) {
    # 3. Opcodes
    Step 'Opcodes and DLL functions (ScnTools opcheck)'
    $op = Run 'opcheck' @('opcheck', '-o', $Out, $Folder)
    $op.Lines | Where-Object { $_ -match 'opcodes,|DLL functions,' } | ForEach-Object { Note $_.Trim() }
    $op.Lines | Where-Object { $_ -match '^\s{4}([0-9A-F]{4}|\S+!\S+) x\d+' } | ForEach-Object {
        Finding "Missing: $($_.Trim()) - adding-a-game.md, ""Missing opcodes""."
    }

    # 4. Embedded routines
    Step 'Embedded x86 routines (ScnTools routines)'
    $rArgs = @('routines', $Folder)
    if ($Like.Count -gt 0) { $rArgs += '--like'; $rArgs += $Like }
    $rt = Run 'routines' $rArgs
    $rt.Lines | Select-Object -Last 1 | ForEach-Object { Note $_ }
    $rt.Lines | Where-Object { $_ -match 'same shape as|instructions differ from' } | ForEach-Object { Note ("    " + $_.Trim()) }
    if ($Like.Count -eq 0) { Note '(no -Like / -LikeFile: routines without a C# version were not matched against other games)' }

    # 5. From the start
    Step "From the start (ScnBoot, $BootFrames frames)"
    $boot = Join-Path $Out 'boot.txt'
    Push-Location $Out
    try { & $scnboot $Folder $BootFrames 2>&1 | ForEach-Object { "$_" } | Set-Content -LiteralPath $boot -Encoding UTF8 } finally { Pop-Location }
    $bootLines = @(Get-Content -LiteralPath $boot -Encoding UTF8)
    $bootLines | Select-Object -First 2 | ForEach-Object { Note $_ }
    $stop = $bootLines | Where-Object { $_ -match '^Frame \d+:|message box|Unhandled|not supported' } | Select-Object -First 2
    if ($stop) {
        $stop | ForEach-Object { Note $_ }
        Finding "It stops from the start: $($stop[0]) (boot.txt) - adding-a-game.md, ""It stops""."
    } elseif ($bootLines -match 'Still running after') {
        Note 'Runs to the end of the frames.'
    }

    # 6. Sweep
    if (-not $NoSweep) {
        Step 'Sweep (tests\sweep.ps1)'
        $sweepOut = Join-Path $Out 'sweep'
        & (Join-Path $PSScriptRoot 'sweep.ps1') -Folder $Folder -Out $sweepOut *>&1 | ForEach-Object { "$_" } |
            Set-Content -LiteralPath (Join-Path $Out 'sweep.txt') -Encoding UTF8
        $sweepLines = @(Get-Content -LiteralPath (Join-Path $Out 'sweep.txt') -Encoding UTF8)
        $sweepLines | Where-Object { $_ -match 'scenarios in|^\s+(done|stopped|stuck|no summary|out of frames)' } | ForEach-Object { Note $_.Trim() }
        $bad = @($sweepLines | Where-Object { $_ -match '^\s{4}\S+\s+(stopped|stuck|no summary|out of frames)' })
        foreach ($b in $bad | Select-Object -First 10) { Finding "Sweep: $($b.Trim()) - adding-a-game.md, ""It stops"" / ""It stands still""." }
        $slow = $sweepLines | Where-Object { $_ -match '^\s{4}(\d+) ms at' } | Select-Object -First 3
        $slow | ForEach-Object { Note ("slowest: " + $_.Trim()) }
        if (($slow | Select-Object -First 1) -match '^\s{4}(\d+) ms' -and [int]$Matches[1] -gt 1000) {
            Finding 'Frames over a second: adding-a-game.md, "Slow frames".'
        }
        # Routines the sweep ran without a C# version
        $ran = @{}
        Get-ChildItem -LiteralPath (Join-Path $sweepOut 'logs') -Filter '*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            foreach ($line in Get-Content -LiteralPath $_.FullName -Encoding UTF8) {
                if ($line -match '^\s+routine ([0-9A-F]{8}) ([0-9A-F]{40}): (interpreter|translated)') { $ran["$($Matches[1]) $($Matches[2])"] = $Matches[3] }
            }
        }
        foreach ($key in $ran.Keys) { Finding "The sweep ran routine $key without a C# version ($($ran[$key])) - adding-a-game.md, ""Routines without C#""." }
        if ($ran.Count -eq 0) { Note 'Every routine the sweep ran has a C# version.' }
    }
}

# The report
$head = @("# $(Split-Path -Leaf $Folder)", '', "Tried on $(Get-Date -Format 'yyyy-MM-dd HH:mm') by tests\newgame.ps1$(if ($scheme) { "; scheme: $scheme" })", '')
if ($findings.Count -eq 0) {
    $head += '**Nothing to do: it plays through.** Update the docs (adding-a-game.md, "When it plays through").'
} else {
    $head += "**$($findings.Count) to do:**"
    $head += ($findings | ForEach-Object { "- $_" })
}
$file = Join-Path $Out 'report.md'
[IO.File]::WriteAllLines($file, [string[]]($head + $report), [Text.UTF8Encoding]::new($false))
Write-Host ''
Write-Host "$($findings.Count) to do. Report: $file"
if ($findings.Count -gt 0) { exit 2 }
