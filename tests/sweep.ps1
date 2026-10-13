param(
    [Parameter(Mandatory)][string]$Folder,
    [string]$Out = '',
    [int]$Parallel = 4,
    [int]$Frames = 30000
)
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
# choices not taken, or lines shown with the one before (two lines of a message).

$scnboot = Join-Path $PSScriptRoot 'OpenShiina.ScnBoot\bin\Release\net10.0\scnboot.exe'
if (-not (Test-Path -LiteralPath $scnboot)) { throw "Build ScnBoot first (Release): $scnboot" }
if (-not $Out) { $Out = 'sweep-' + (Split-Path -Leaf $Folder) }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
New-Item -ItemType Directory -Force (Join-Path $Out 'logs') | Out-Null

$env:SCNBOOT_LIST = '1'
$list = & $scnboot $Folder | ForEach-Object { ($_ -split "`t")[0] }
Remove-Item Env:SCNBOOT_LIST
$env:SCNBOOT_SWEEP = '1'; $env:SCNBOOT_CACHE = '256'; $env:SCNBOOT_STALL = '30'

$running = @{}
$seconds = @{}
$queue = [Collections.Queue]::new(@($list))
$clock = [Diagnostics.Stopwatch]::StartNew()
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

$rows = foreach ($name in $list) {
    $log = Get-Content (Join-Path $Out "logs\$name.txt") -Encoding UTF8 -ErrorAction SilentlyContinue
    $sweep = $log | Where-Object { $_ -like '`[sweep`]*' } | Select-Object -First 1
    $why = $log | Where-Object { $_ -match 'message box|^Frame \d+:|\[stall\]|Unhandled' } | Select-Object -First 1
    $f = if ($sweep) { $sweep -split "`t" } else { @('', $name, 'no summary', '', '0/0', '', '', '') }
    [pscustomobject]@{
        Scenario = $name; Result = $f[2]; Frames = $f[3]; Lines = $f[4]; Last = $f[5]; Slowest = $f[6]
        Heap = $f[7]; Error = "$why"; Seconds = $seconds[$name]
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
'  stopped, stuck, or under 90% of the lines:'
$rows | Where-Object { $_.Result -notlike 'done*' -or (All $_) -gt 0 -and (Shown $_) / (All $_) -lt 0.9 } | ForEach-Object {
    "    {0,-18} {1,-22} {2,-10} last {3} {4}" -f $_.Scenario, $_.Result, $_.Lines, $_.Last, $_.Error
}
'  slowest frames:'
$rows | Sort-Object { [int](($_.Slowest -split ' ')[0]) } -Descending | Select-Object -First 3 |
    ForEach-Object { "    {0}  ({1})" -f $_.Slowest, $_.Scenario }
