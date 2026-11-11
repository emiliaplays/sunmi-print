param([string]$Makensis)

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$inputDirectory = Join-Path $dist 'installer-input'
$version = '1.0.0'

if (!$Makensis) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($command) { $Makensis = $command.Source }
}
if (!$Makensis) {
    $toolCache = Join-Path $env:LOCALAPPDATA 'SunmiPrint\build-tools\nsis'
    $Makensis = Get-ChildItem -LiteralPath $toolCache -Recurse -Filter makensis.exe -File -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$Makensis) {
    $Makensis = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ } |
        ForEach-Object { Join-Path $_ 'NSIS\makensis.exe' } | Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if (!$Makensis) {
    throw 'Installiere NSIS oder gib den Pfad zu makensis.exe mit -Makensis an.'
}

& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $inputDirectory
$setup = Join-Path $dist "SunmiPrint-Setup-$version.exe"
Push-Location (Join-Path $PSScriptRoot 'installer')
try {
    & $Makensis '/V3' '/INPUTCHARSET' 'UTF8' "/DAPP_VERSION=$version" "/DINPUT_DIR=$inputDirectory" "/DSOURCE_DIR=$PSScriptRoot" "/DSETUP_PATH=$setup" 'SunmiPrint.nsi'
    if ($LASTEXITCODE -ne 0) { throw "NSIS meldet Exitcode $LASTEXITCODE." }
} finally {
    Pop-Location
}
Write-Output $setup
