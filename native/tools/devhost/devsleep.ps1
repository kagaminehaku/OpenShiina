param([Parameter(Mandatory)] [string]$Address)
# Puts the Switch to sleep through devhost (main.cpp, "SLEP"): at the end of the runs, so that it does
# not stay awake with the screen on (devhost keeps sleep off while it waits):
#   devsleep.ps1 -Address <Switch IP>
$ErrorActionPreference = 'Stop'
$client = New-Object System.Net.Sockets.TcpClient
$client.ReceiveTimeout = 10000
$client.Connect($Address, 28280)
try {
    $stream = $client.GetStream()
    $request = [Text.Encoding]::ASCII.GetBytes('SLEP')
    $stream.Write($request, 0, $request.Length)
    $answer = New-Object byte[] 4
    $at = 0
    while ($at -lt 4) {
        $got = $stream.Read($answer, $at, 4 - $at)
        if ($got -le 0) { break }
        $at += $got
    }
    if ($at -ne 4 -or [BitConverter]::ToInt32($answer, 0) -ne 0) { throw 'devhost did not answer (an older devhost has no "SLEP")' }
    'The Switch goes to sleep'
}
finally {
    $client.Close()
}
