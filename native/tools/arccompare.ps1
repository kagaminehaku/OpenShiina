# Every file of each game's archives decoded by the C# player (ScnTools extract) and by the C++
# core (arcdump), compared by SHA-256 (arccompare.py). The C# files go to a temporary folder, removed
# after each game.
#   arccompare.ps1 -Work <temp folder> <game folder>...
param(
    [Parameter(Mandatory)] [string] $Work,
    [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Games
)
$repo = Split-Path (Split-Path $PSScriptRoot)
$scnTools = Join-Path $repo 'tools\ScnTools\bin\Release\net10.0\ScnTools.exe'
$arcdump = Join-Path $repo 'native\build\arcdump.exe'
$formats = Join-Path $repo 'src\OpenShiina.Core\Data\Formats.Json'
New-Item -ItemType Directory -Force $Work | Out-Null
$failed = 0
foreach ($game in $Games) {
    $name = Split-Path $game -Leaf
    $cs = Join-Path $Work 'cs'
    if (Test-Path $cs) { Remove-Item -Recurse -Force $cs }
    $dump = Join-Path $Work "$name.txt"
    Write-Output "== $name"
    $t = [Diagnostics.Stopwatch]::StartNew()
    & $scnTools extract $game '*' $cs *> (Join-Path $Work "$name.cs.log")
    $csTime = $t.Elapsed.TotalSeconds
    $t.Restart()
    & $arcdump $formats $game $dump 2> (Join-Path $Work "$name.native.log")
    $nativeTime = $t.Elapsed.TotalSeconds
    Write-Output ("   C# {0:N1} s, C++ {1:N1} s" -f $csTime, $nativeTime)
    & python -I (Join-Path $PSScriptRoot 'arccompare.py') $dump $cs
    if ($LASTEXITCODE -ne 0) { $failed++ }
    Remove-Item -Recurse -Force $cs -ErrorAction SilentlyContinue
}
Write-Output "$($Games.Count - $failed) of $($Games.Count) games the same"
exit $failed
