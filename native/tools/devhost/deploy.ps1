param([Parameter(Mandatory)] [string]$Address, [Parameter(Mandatory)] [string]$Keys, [Parameter(Mandatory)] [string]$Work,
      [string]$Version = '0.0.0', [string]$Build = (Join-Path $PSScriptRoot '..\..\build-switch'), [switch]$NoNsp)
# A Switch build onto the console through devhost (main.cpp), nothing started: the NSP packed
# (..\nsp\build.ps1, the console's prod.keys in -Keys, never kept here), the NSPs in
# sdmc:/OpenShiina deleted and the new one put there (OpenShiina-<date>-<time>-switch.nsp, to
# install with DBI), and the new OpenShiina.nro and devhost.nro put in their places ('+': saved,
# not started). devhost may run as an applet (from the Album): nothing here needs memory there.
$ErrorActionPreference = 'Stop'
$nxlink = 'C:\msys64\opt\devkitpro\tools\bin\nxlink.exe'
$devget = Join-Path $PSScriptRoot 'devget.ps1'
$Build = (Resolve-Path $Build).Path

function Send([string]$to, [string]$file) {
    # (nxlink's progress goes to stderr; its exit code tells)
    $out = & cmd /c "`"$nxlink`" -a $Address -p $to `"$file`" 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "nxlink could not send $file ($($out -join ' '))" }
    Start-Sleep -Seconds 2
}

if (-not $NoNsp) {
    & (Join-Path $PSScriptRoot '..\nsp\build.ps1') -Keys $Keys -Work $Work -Version $Version -Build $Build | Out-Null
    $packed = Get-ChildItem (Join-Path $Work 'hacbrewpack_nsp') -Filter *.nsp | Select-Object -First 1
    $name = 'OpenShiina-' + (Get-Date -Format 'yyyy-MM-dd-HHmm') + '-switch.nsp'
    foreach ($old in & $devget -Address $Address -Path sdmc:/OpenShiina | Where-Object { $_.Kind -eq 'F' -and $_.Name -like '*.nsp' }) {
        & $devget -Address $Address -Path "sdmc:/OpenShiina/$($old.Name)" -Delete | Out-Null
        "deleted $($old.Name)"
    }
    Send "@$name" $packed.FullName
    $there = & $devget -Address $Address -Path sdmc:/OpenShiina | Where-Object Name -eq $name
    if (-not $there -or $there.Size -ne $packed.Length) { throw "$name did not arrive whole" }
    "sent $name ($($packed.Length) bytes)"
}
Send '+openshiina/OpenShiina.nro' (Join-Path $Build 'OpenShiina.nro')
'sent openshiina/OpenShiina.nro'
Send '+openshiina/devhost.nro' (Join-Path $Build 'devhost.nro')
Send '+devhost.nro' (Join-Path $Build 'devhost.nro')
'sent devhost.nro (both places)'
