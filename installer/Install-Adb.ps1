$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$android = Join-Path $env:LOCALAPPDATA 'Android'
$destination = Join-Path $android 'platform-tools'
if ((Test-Path -LiteralPath (Join-Path $destination 'adb.exe')) -or
    (Get-Command adb.exe -ErrorAction SilentlyContinue)) {
    Write-Output 'ADB ist bereits installiert.'
    exit 0
}

$repositoryUrl = 'https://dl.google.com/android/repository/'
$work = Join-Path ([IO.Path]::GetTempPath()) ('sunmi-adb-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $metadata = [xml](Invoke-WebRequest -UseBasicParsing -Uri ($repositoryUrl + 'repository2-3.xml') -TimeoutSec 30).Content
    $package = $metadata.DocumentElement.remotePackage | Where-Object path -eq 'platform-tools' | Select-Object -First 1
    $archive = $package.archives.archive | Where-Object { $_.'host-os' -eq 'windows' } |
        Select-Object -First 1 -ExpandProperty complete
    if ($archive.url -notmatch '\Aplatform-tools_[A-Za-z0-9._-]+-win(?:dows)?\.zip\z' -or
        $archive.checksum.InnerText -notmatch '\A[0-9a-fA-F]{40}\z') {
        throw 'Google stellt keinen passenden Download mit Prüfsumme bereit.'
    }
    $zip = Join-Path $work 'platform-tools.zip'
    Invoke-WebRequest -UseBasicParsing -Uri ($repositoryUrl + $archive.url) -OutFile $zip -TimeoutSec 90
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA1).Hash -ne $archive.checksum.InnerText) {
        throw 'Die Prüfsumme des ADB-Downloads stimmt nicht.'
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $work
    $source = (Resolve-Path -LiteralPath (Join-Path $work 'platform-tools')).Path
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    if ($source -ne (Join-Path $resolvedWork 'platform-tools') -or
        !(Test-Path -LiteralPath (Join-Path $source 'adb.exe'))) {
        throw 'Das Archiv enthält keine gültigen Platform-Tools.'
    }
    New-Item -ItemType Directory -Path $android -Force | Out-Null
    $resolvedAndroid = (Resolve-Path -LiteralPath $android).Path
    if ($resolvedAndroid -ne [IO.Path]::GetFullPath($android)) {
        throw 'Der Installationspfad für ADB ist ungültig.'
    }
    if (!(Test-Path -LiteralPath $destination)) {
        [IO.Directory]::Move($source, (Join-Path $resolvedAndroid 'platform-tools'))
    } elseif (!(Test-Path -LiteralPath (Join-Path $destination 'adb.exe'))) {
        throw 'Der ADB-Zielordner ist bereits vorhanden. Installiere die Platform-Tools manuell.'
    }
    Write-Output 'ADB ist installiert.'
} finally {
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    if ($resolvedWork -eq [IO.Path]::GetFullPath($work)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
