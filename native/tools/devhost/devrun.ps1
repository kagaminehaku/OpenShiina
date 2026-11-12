param([Parameter(Mandatory)] [string]$Address, [Parameter(Mandatory)] [string]$Script, [string]$Out,
      [string]$Nro = 'openshiina/OpenShiina.nro', [int]$Wait = 1800, [string]$Compare)
# A test run of the player on the Switch with no one at the console (player/TestRun.h): the script
# (game=, frames=, mouse=, press=, every=, clock=) goes to sdmc:/OpenShiina/Tests/<name>.txt
# (devput.ps1), devhost starts the player with "test:<name>" ("RUNF", main.cpp), and when devhost
# answers again (the player went back to it) its log and pictures come back into -Out:
#   devrun.ps1 -Address <Switch IP> -Script nuki2.txt -Out runs\nuki2
# devhost must be open in a mode with the memory for a game (from the OpenShiina title or hbmenu
# held over a game, not from the Album). -Compare <folder of scnrun's pictures for the same script>
# compares the pictures pixel for pixel (native/tools/scnrun/screencompare.py).
$ErrorActionPreference = 'Stop'
$name = [IO.Path]::GetFileNameWithoutExtension($Script)
if ($name -notmatch '^[A-Za-z0-9_.-]+$') { throw "The script's name must be Latin letters, digits, _ . - (the Switch's folders): $name" }
if (-not $Out) { $Out = Join-Path (Get-Location) "run-$name" }
New-Item -ItemType Directory -Force $Out | Out-Null
$devput = Join-Path $PSScriptRoot 'devput.ps1'
$devget = Join-Path $PSScriptRoot 'devget.ps1'

# The script, then the run
& $devput -Address $Address -From $Script -To "Tests/$name.txt" | Out-Null
$client = New-Object System.Net.Sockets.TcpClient
$client.ReceiveTimeout = 30000
$client.Connect($Address, 28280)
try {
    $stream = $client.GetStream()
    $path = [Text.Encoding]::UTF8.GetBytes($Nro)
    $arguments = [Text.Encoding]::UTF8.GetBytes("test:$name`0")
    $request = [byte[]]([Text.Encoding]::ASCII.GetBytes('RUNF') + [BitConverter]::GetBytes([int32]$path.Length) + $path +
                        [BitConverter]::GetBytes([int32]$arguments.Length) + $arguments)
    $stream.Write($request, 0, $request.Length)
    $answer = New-Object byte[] 4
    $got = 0
    while ($got -lt 4) {
        $n = $stream.Read($answer, $got, 4 - $got)
        if ($n -le 0) { throw 'devhost closed the connection' }
        $got += $n
    }
    if ([BitConverter]::ToInt32($answer, 0) -ne 0) { throw "devhost has no sdmc:/switch/$Nro" }
}
finally { $client.Dispose() }
Write-Host "Started $Nro test:$name"

# devhost answers again once the player went back to it
$clock = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds 10
$back = $false
while ($clock.Elapsed.TotalSeconds -lt $Wait) {
    $probe = New-Object System.Net.Sockets.TcpClient
    try {
        $connect = $probe.ConnectAsync($Address, 28280)
        if ($connect.Wait(3000) -and $probe.Connected) { $back = $true; break }
    }
    catch { }
    finally { $probe.Dispose() }
    Start-Sleep -Seconds 5
}
if (-not $back) { throw "devhost did not come back in $Wait s (the player may have stopped: see its screen)" }
Write-Host ("Back in {0:N0} s" -f $clock.Elapsed.TotalSeconds)
# (a connection that sends nothing holds devhost 10 s: let it go before the next)
Start-Sleep -Seconds 11

& $devget -Address $Address -Path 'sdmc:/OpenShiina/player.log' -Out (Join-Path $Out 'player.log') | Out-Null
$pictures = @(& $devget -Address $Address -Path "sdmc:/OpenShiina/Tests/$name" | Where-Object { $_.Kind -eq 'F' -and $_.Name -like '*.png' })
foreach ($picture in $pictures) {
    & $devget -Address $Address -Path "sdmc:/OpenShiina/Tests/$name/$($picture.Name)" -Out (Join-Path $Out $picture.Name) | Out-Null
}
Write-Host "$($pictures.Count) pictures and player.log in $Out"
Get-Content -LiteralPath (Join-Path $Out 'player.log') | Select-String '^\[test\]|^\[memory\] before|Still running|quit after|error|Error|Out of memory' |
    ForEach-Object { $_.Line }
if ($Compare -and $pictures.Count -gt 0) {
    $comparer = Join-Path $PSScriptRoot '..\scnrun\screencompare.py'
    # (its "missing" and "differ" lines on stderr are its answer, not an error)
    $ErrorActionPreference = 'Continue'
    & python -I $comparer $Compare $Out 2>&1 | ForEach-Object { "$_" }
}
