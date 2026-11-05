param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$Out = '',
    [int]$Parallel = 4,
    [int]$Frames = 30000,
    [string[]]$Only = @(),
    [string]$Again = ''
)
# -Only: those scenarios alone; -Again <summary.tsv of an earlier sweep>: its scenarios that did
# not end "done" alone (a fix for the ones stuck or stopped, without running the others again)
# Sweeps a game: every scenario with message lines run through by ScnBoot (SCNBOOT_PLAYFROM at its
# first line, SCNBOOT_SWEEP: skip held, choices made), a process each, $Parallel at a time. Writes
# $Out\logs\<scenario>.txt and $Out\summary.tsv, and prints how the scenarios ended, the lines
# shown of all, the heap's peak, the scenarios that stopped or left lines, and the slowest frames.
#
#   .\tests\sweep.ps1 -Folder "D:\Games\Maki Fes!" [-Out sweep-makifes] [-Parallel 4]
#
# Build ScnBoot first (Release). A scenario ends "done" when a line of another one shows or every
# line of it has, "stuck" when no new line shows for 9000 frames (a movie playing is not counted),
# "stopped" when the scripts stop (an error: its log says which). Lines left are the branches of
# choices not taken, or lines shown with the one before (two lines of a message). A scenario stuck
# is run again with the grid's lower right left out, then with clicks only in the middle of the
# screen (SCNBOOT_SWEEP_CLICKS=safe, center): a place of the grid the sweep clicks can be a button
# that opens a screen of the game's own.

$scnboot = Join-Path $PSScriptRoot 'OpenShiina.ScnBoot\bin\Release\net10.0\scnboot.exe'
if (-not (Test-Path -LiteralPath $scnboot)) { throw "Build ScnBoot first (Release): $scnboot" }
if (-not $Out) { $Out = 'sweep-' + (Split-Path -Leaf $Folder) }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
New-Item -ItemType Directory -Force (Join-Path $Out 'logs') | Out-Null

$env:SCNBOOT_LIST = '1'
$list = & $scnboot $Folder | ForEach-Object { ($_ -split "`t")[0] }
Remove-Item Env:SCNBOOT_LIST
if ($Again) {
    $Only += @(Import-Csv -LiteralPath $Again -Delimiter "`t" | Where-Object { $_.Result -notlike 'done*' } | ForEach-Object Scenario)
    if ($Only.Count -eq 0) { "Every scenario of $Again ended done."; return }
}
if ($Only.Count -gt 0) { $list = @($list | Where-Object { $Only -contains $_ }) }
$env:SCNBOOT_SWEEP = '1'; $env:SCNBOOT_CACHE = '256'; $env:SCNBOOT_STALL = '30'

$seconds = @{}
$clock = [Diagnostics.Stopwatch]::StartNew()
function RunAll($names) {
    $running = @{}
    $queue = [Collections.Queue]::new(@($names))
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
            $name = $queue.Dequeue()
            $env:SCNBOOT_PLAYFROM = "${name}:1"
            $log = Join-Path $Out "logs\$name"
            $running[$name] = Start-Process -FilePath $scnboot -ArgumentList "`"$Folder`"", $Frames -NoNewWindow -PassThru `
                -RedirectStandardOutput "$log.txt" -RedirectStandardError "$log.err"
            $seconds[$name] = $clock.Elapsed.TotalSeconds
        }
        Start-Sleep -Milliseconds 300
        foreach ($name in @($running.Keys)) {
            if ($running[$name].HasExited) {
                $running.Remove($name)
                $seconds[$name] = [Math]::Round($clock.Elapsed.TotalSeconds - $seconds[$name], 1)
            }
        }
    }
}
function Stuck($names) {
    @($names | Where-Object {
        (Get-Content (Join-Path $Out "logs\$_.txt") -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -like '`[sweep`]*' } |
            Select-Object -First 1) -match "^\[sweep\]`t[^`t]*`tstuck`t" })
}
RunAll $list
$retried = @{}
$stuck = Stuck $list
foreach ($clicks in 'safe', 'center') {
    if ($stuck.Count -eq 0) { break }
    $env:SCNBOOT_SWEEP_CLICKS = $clicks
    RunAll $stuck
    Remove-Item Env:SCNBOOT_SWEEP_CLICKS
    foreach ($name in $stuck) { $retried[$name] = $clicks }
    $stuck = Stuck $stuck
}

$rows = foreach ($name in $list) {
    $log = Get-Content (Join-Path $Out "logs\$name.txt") -Encoding UTF8 -ErrorAction SilentlyContinue
    $sweep = $log | Where-Object { $_ -like '`[sweep`]*' } | Select-Object -First 1
    $why = $log | Where-Object { $_ -match 'message box|^Frame \d+:|\[stall\]|Unhandled' } | Select-Object -First 1
    $f = if ($sweep) { $sweep -split "`t" } else { @('', $name, 'no summary', '', '0/0', '', '', '') }
    [pscustomobject]@{
        Scenario = $name; Result = $f[2]; Frames = $f[3]; Lines = $f[4]; Last = $f[5]; Slowest = $f[6]
        Heap = $f[7]; Error = "$why"; Seconds = $seconds[$name]; Clicks = $(if ($retried[$name]) { $retried[$name] } else { 'grid' })
    }
}
$rows | Export-Csv (Join-Path $Out 'summary.tsv') -Delimiter "`t" -NoTypeInformation -Encoding UTF8

function Shown($r) { [int](($r.Lines -split '/')[0]) }
function All($r) { [int](($r.Lines -split '/')[1]) }
$shown = ($rows | ForEach-Object { Shown $_ } | Measure-Object -Sum).Sum
$all = ($rows | ForEach-Object { All $_ } | Measure-Object -Sum).Sum
$heap = ($rows | ForEach-Object { if ($_.Heap -match '(\d+) MB') { [int]$Matches[1] } } | Measure-Object -Maximum).Maximum
"{0} scenarios in {1:N0} s, lines {2}/{3} ({4:P1}), heap peak {5} MB" -f $rows.Count, $clock.Elapsed.TotalSeconds, $shown, $all, ($shown / [Math]::Max(1, $all)), $heap
$rows | Group-Object Result | ForEach-Object { "  {0}: {1}" -f $_.Name, $_.Count }
if ($retried.Count -gt 0) { "  run again with other clicks (stuck with the grid): {0}" -f $retried.Count }
'  stopped, stuck, or under 90% of the lines:'
$rows | Where-Object { $_.Result -notlike 'done*' -or ((All $_) -gt 0 -and (Shown $_) / (All $_) -lt 0.9) } | ForEach-Object {
    "    {0,-18} {1,-22} {2,-10} last {3} {4}" -f $_.Scenario, $_.Result, $_.Lines, $_.Last, $_.Error
}
'  slowest frames:'
$rows | Sort-Object { [int](($_.Slowest -split ' ')[0]) } -Descending | Select-Object -First 3 |
    ForEach-Object { "    {0}  ({1})" -f $_.Slowest, $_.Scenario }
