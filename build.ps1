param(
    [switch]$AndroidOnly,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$cache = Join-Path $env:LOCALAPPDATA 'SunmiPrint\build-tools'
$dist = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $cache, $dist -Force | Out-Null

function Invoke-BuildTool([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Build tool failed: $Executable ($LASTEXITCODE)" }
}

function Read-Checksum([string]$Uri) {
    $content = (Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 30).Content
    if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
    return ($content.Trim() -split '\s+')[0]
}

function Save-VerifiedDownload([string]$Uri, [string]$File, [string]$Algorithm, [string]$Checksum) {
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $File -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $File -Algorithm $Algorithm).Hash -ne $Checksum) {
        throw "Checksum mismatch: $Uri"
    }
}

function Expand-ToolArchive([string]$Uri, [string]$Directory, [string]$Algorithm, [string]$Checksum) {
    $archive = Join-Path $cache ([guid]::NewGuid().ToString() + '.zip')
    try {
        Save-VerifiedDownload $Uri $archive $Algorithm $Checksum
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        Invoke-BuildTool 'tar.exe' @('-xf', $archive, '-C', $Directory)
    } finally {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    }
}

$jdkDirectory = Join-Path $cache 'jdk17'
$jdk = Get-ChildItem -LiteralPath $jdkDirectory -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin\javac.exe') } |
    Select-Object -First 1
if (!$jdk) {
    $architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'aarch64' } else { 'x64' }
    $uri = "https://aka.ms/download-jdk/microsoft-jdk-17-windows-$architecture.zip"
    Expand-ToolArchive $uri $jdkDirectory SHA256 (Read-Checksum ($uri + '.sha256sum.txt'))
    $jdk = Get-ChildItem -LiteralPath $jdkDirectory -Directory | Select-Object -First 1
}
$javaBin = Join-Path $jdk.FullName 'bin'
$androidJar = Join-Path $cache 'platform35\android-35\android.jar'
$androidTools = Join-Path $cache 'build-tools35\android-15'
if (!(Test-Path -LiteralPath $androidJar) -or !(Test-Path -LiteralPath (Join-Path $androidTools 'aapt2.exe'))) {
    $repositoryUri = 'https://dl.google.com/android/repository/'
    $repository = [xml](Invoke-WebRequest -UseBasicParsing -Uri ($repositoryUri + 'repository2-3.xml') -TimeoutSec 30).Content
    foreach ($item in @(
        @{ Package = 'platforms;android-35'; Directory = 'platform35'; Check = $androidJar },
        @{ Package = 'build-tools;35.0.0'; Directory = 'build-tools35'; Check = (Join-Path $androidTools 'aapt2.exe') }
    )) {
        if (Test-Path -LiteralPath $item.Check) { continue }
        $package = $repository.DocumentElement.remotePackage | Where-Object path -eq $item.Package | Select-Object -First 1
        $asset = $package.archives.archive | Where-Object { !$_.'host-os' -or $_.'host-os' -eq 'windows' } |
            Select-Object -First 1 -ExpandProperty complete
        Expand-ToolArchive ($repositoryUri + $asset.url) (Join-Path $cache $item.Directory) SHA1 $asset.checksum.InnerText
    }
}

$library = Join-Path $cache 'sunmi-1.0.24'
$sunmiJar = Join-Path $library 'classes.jar'
if (!(Test-Path -LiteralPath $sunmiJar)) {
    $uri = 'https://repo.maven.apache.org/maven2/com/sunmi/printerlibrary/1.0.24/printerlibrary-1.0.24.aar'
    $archive = Join-Path $cache 'sunmi-printer-1.0.24.aar'
    Save-VerifiedDownload $uri $archive SHA1 (Read-Checksum ($uri + '.sha1'))
    New-Item -ItemType Directory -Path $library -Force | Out-Null
    Invoke-BuildTool 'tar.exe' @('-xf', $archive, '-C', $library, 'classes.jar')
}

$key = Join-Path $cache 'bridge-signing.p12'
$passwordFile = Join-Path $cache 'bridge-signing.password'
if (!(Test-Path -LiteralPath $passwordFile)) {
    [IO.File]::WriteAllText($passwordFile, [guid]::NewGuid().ToString('N'))
}
$env:SUNMI_BUILD_KEY_PASSWORD = [IO.File]::ReadAllText($passwordFile)
$work = Join-Path $cache ('build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if (!(Test-Path -LiteralPath $key)) {
        Invoke-BuildTool (Join-Path $javaBin 'keytool.exe') @('-genkeypair', '-keystore', $key,
            '-storepass:env', 'SUNMI_BUILD_KEY_PASSWORD', '-alias', 'bridge', '-keyalg', 'RSA',
            '-keysize', '2048', '-validity', '10000', '-dname', 'CN=SUNMI Print Local Build')
    }

    $classDirectory = Join-Path $work 'classes'
    New-Item -ItemType Directory -Path $classDirectory | Out-Null
    Invoke-BuildTool (Join-Path $javaBin 'javac.exe') @('--release', '8', '-encoding', 'UTF-8',
        '-classpath', "$androidJar;$sunmiJar", '-d', $classDirectory, (Join-Path $PSScriptRoot 'android\PrintBridge.java'))
    $classJar = Join-Path $work 'bridge.jar'
    Invoke-BuildTool (Join-Path $javaBin 'jar.exe') @('cf', $classJar, '-C', $classDirectory, '.')
    Invoke-BuildTool (Join-Path $javaBin 'java.exe') @('-cp', (Join-Path $androidTools 'lib\d8.jar'),
        'com.android.tools.r8.D8', '--release', '--min-api', '23', '--lib', $androidJar, '--output', $work,
        $classJar, $sunmiJar)
    $unsigned = Join-Path $work 'unsigned.apk'
    $aligned = Join-Path $work 'aligned.apk'
    Invoke-BuildTool (Join-Path $androidTools 'aapt2.exe') @('link', '-I', $androidJar, '--manifest',
        (Join-Path $PSScriptRoot 'android\AndroidManifest.xml'), '-o', $unsigned)
    Push-Location $work
    try {
        Invoke-BuildTool (Join-Path $androidTools 'aapt.exe') @('add', $unsigned, 'classes.dex')
    } finally {
        Pop-Location
    }
    Invoke-BuildTool (Join-Path $androidTools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)
    $apk = Join-Path $dist 'SunmiBridge.apk'
    $signer = Join-Path $androidTools 'lib\apksigner.jar'
    Invoke-BuildTool (Join-Path $javaBin 'java.exe') @('-jar', $signer, 'sign', '--ks', $key, '--ks-key-alias', 'bridge',
            '--ks-pass', 'env:SUNMI_BUILD_KEY_PASSWORD', '--v4-signing-enabled', 'false', '--out', $apk, $aligned)
    Invoke-BuildTool (Join-Path $javaBin 'java.exe') @('-jar', $signer, 'verify', '--min-sdk-version', '23', $apk)

    if (!$AndroidOnly) {
        $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
        $windows = Join-Path $PSScriptRoot 'windows'
        $output = Join-Path $dist 'SunmiPrint.exe'
        $arguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$output",
            "/resource:$apk,SunmiBridge.apk", "/resource:$windows\MainWindow.xaml,MainWindow.xaml",
            "/resource:$windows\SettingsWindow.xaml,SettingsWindow.xaml", "/reference:$framework\System.Xaml.dll",
            "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll",
            "/reference:$framework\WPF\PresentationFramework.dll", '/reference:System.Drawing.dll',
            '/reference:System.Xml.dll', '/reference:System.Core.dll')
        $arguments += @(Get-ChildItem -LiteralPath $windows -Filter '*.cs' | ForEach-Object FullName)
        Invoke-BuildTool (Join-Path $framework 'csc.exe') $arguments
        Write-Output $output
    }
} finally {
    Remove-Item Env:SUNMI_BUILD_KEY_PASSWORD -ErrorAction SilentlyContinue
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    $resolvedCache = (Resolve-Path -LiteralPath $cache).Path.TrimEnd('\') + '\'
    if ($resolvedWork.StartsWith($resolvedCache, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
