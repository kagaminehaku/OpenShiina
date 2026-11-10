param([Parameter(Mandatory)] [string]$Address, [Parameter(Mandatory)] [string]$Path, [string]$Out)
# Reads a file or a folder of the Switch's SD card from devhost (main.cpp, "GETF"):
#   devget.ps1 -Address <Switch IP> -Path sdmc:/OpenShiina/Games            (lists the folder)
#   devget.ps1 -Address <Switch IP> -Path sdmc:/OpenShiina/player.log -Out player.log
# A file is saved to -Out (or to its own name in the current folder).
$ErrorActionPreference = 'Stop'
$client = New-Object System.Net.Sockets.TcpClient
$client.ReceiveTimeout = 30000
$client.SendTimeout = 30000
$client.Connect($Address, 28280)
$stream = $client.GetStream()
try {
    function Read-Exactly([int]$count) {
        $buffer = New-Object byte[] $count
        $at = 0
        while ($at -lt $count) {
            $got = $stream.Read($buffer, $at, $count - $at)
            if ($got -le 0) { throw "devhost closed the connection" }
            $at += $got
        }
        return ,$buffer
    }
    $name = [Text.Encoding]::UTF8.GetBytes($Path)
    $request = [byte[]]([Text.Encoding]::ASCII.GetBytes('GETF') + [BitConverter]::GetBytes([int32]$name.Length) + $name)
    $stream.Write($request, 0, $request.Length)
    $status = [BitConverter]::ToInt32((Read-Exactly 4), 0)
    if ($status -lt 0) { throw "devhost has no $Path (its screen tells why)" }
    if ($status -eq 1) {
        $length = [BitConverter]::ToInt32((Read-Exactly 4), 0)
        $text = if ($length -gt 0) { [Text.Encoding]::UTF8.GetString((Read-Exactly $length)) } else { '' }
        $text.TrimEnd("`n").Split("`n") | Where-Object { $_ } | ForEach-Object {
            $kind, $size, $entry = $_.Split("`t", 3)
            [pscustomobject]@{ Kind = $kind; Size = [int64]$size; Name = $entry }
        }
        return
    }
    $size = [BitConverter]::ToInt64((Read-Exactly 8), 0)
    if (-not $Out) { $Out = Split-Path $Path -Leaf }
    $Out = [IO.Path]::GetFullPath($Out)
    $file = [IO.File]::Create($Out)
    try {
        $buffer = New-Object byte[] (1024 * 1024)
        $left = $size
        while ($left -gt 0) {
            $got = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left))
            if ($got -le 0) { throw "devhost stopped after $($size - $left) of $size bytes" }
            $file.Write($buffer, 0, $got)
            $left -= $got
        }
    }
    finally { $file.Dispose() }
    [pscustomobject]@{ Saved = $Out; Bytes = $size }
}
finally { $client.Dispose() }
