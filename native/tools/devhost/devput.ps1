param([Parameter(Mandatory)] [string]$Address, [Parameter(Mandatory)] [string]$From, [Parameter(Mandatory)] [string]$To,
      [string[]]$Skip = @())
# Writes a file, or every file of a folder, to the Switch's SD card under sdmc:/OpenShiina through
# devhost (main.cpp, "PUTF"): the bytes as they are, not compressed, so much faster than nxlink's
# uploads (a game's .WAR files are compressed already).
#   devput.ps1 -Address <Switch IP> -From <game folder> -To Games/<Latin name> [-Skip *.dmp]
#   devput.ps1 -Address <Switch IP> -From <file> -To Games/<Latin name>/<file name>
# A folder's files keep their places under -To; a file already there with the same size is passed
# over (a copy cut short goes on where it stopped, a file at a time). -Skip: names left out (wildcards).
# The Switch does not see Japanese folder names written from Windows: -To is a Latin name.
$ErrorActionPreference = 'Stop'
$devget = Join-Path $PSScriptRoot 'devget.ps1'

function Send-File([string]$file, [string]$path) {
    $size = (Get-Item -LiteralPath $file).Length
    $client = New-Object System.Net.Sockets.TcpClient
    $client.SendBufferSize = 4MB
    $client.NoDelay = $true
    $client.SendTimeout = 60000
    $client.ReceiveTimeout = 60000
    $client.Connect($Address, 28280)
    $stream = $client.GetStream()
    $input = [IO.File]::OpenRead($file)
    try {
        $name = [Text.Encoding]::UTF8.GetBytes($path)
        $request = [byte[]]([Text.Encoding]::ASCII.GetBytes('PUTF') + [BitConverter]::GetBytes([int32]$name.Length) + $name +
                            [BitConverter]::GetBytes([int64]$size))
        $stream.Write($request, 0, $request.Length)
        $answer = New-Object byte[] 4
        function Read-Answer {
            $at = 0
            while ($at -lt 4) {
                $got = $stream.Read($answer, $at, 4 - $at)
                if ($got -le 0) { throw "devhost closed the connection" }
                $at += $got
            }
            [BitConverter]::ToInt32($answer, 0)
        }
        if ((Read-Answer) -ne 0) { throw "devhost cannot write $path (its screen tells why)" }
        $buffer = New-Object byte[] 1MB
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (($got = $input.Read($buffer, 0, $buffer.Length)) -gt 0) { $stream.Write($buffer, 0, $got) }
        if ((Read-Answer) -ne 0) { throw "devhost did not keep $path whole (its screen tells why)" }
        $seconds = [Math]::Max($watch.Elapsed.TotalSeconds, 0.001)
        "{0,-40} {1,12:N0} bytes {2,7:N1} s {3,6:N1} MB/s" -f $path, $size, $seconds, ($size / 1MB / $seconds)
    }
    finally {
        $input.Dispose()
        $client.Dispose()
    }
}

# What a folder of the card holds: name -> size of its files (none when the folder is not there)
$listed = @{}
function Get-There([string]$folder) {
    if (-not $listed.ContainsKey($folder)) {
        $files = @{}
        try {
            foreach ($entry in & $devget -Address $Address -Path "sdmc:/OpenShiina/$folder" | Where-Object Kind -eq 'F') {
                $files[$entry.Name] = [int64]$entry.Size
            }
        }
        catch { }
        $listed[$folder] = $files
    }
    $listed[$folder]
}

$To = $To.Trim('/').Replace('\', '/')
$item = Get-Item -LiteralPath $From
if (-not $item.PSIsContainer) {
    Send-File $item.FullName $To
    return
}
$sent = 0
$passed = 0
foreach ($file in Get-ChildItem -LiteralPath $item.FullName -Recurse -File) {
    if ($Skip | Where-Object { $file.Name -like $_ }) { continue }
    $path = "$To/" + $file.FullName.Substring($item.FullName.Length + 1).Replace('\', '/')
    $folder = $path.Substring(0, $path.LastIndexOf('/'))
    $there = Get-There $folder
    if ($there.ContainsKey($file.Name) -and $there[$file.Name] -eq $file.Length) {
        $passed++
        continue
    }
    Send-File $file.FullName $path
    $sent++
}
"$sent sent, $passed already there"
