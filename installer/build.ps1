<#
.SYNOPSIS
    Builds the Kairo MSI: publishes Kairo.exe and Kairo.BrowserHost.exe (self-contained, win-x64),
    adds the browser extension and builds installer\Kairo.Installer.wixproj (WiX Toolset v5).

.DESCRIPTION
    Runs on PowerShell 7 and Windows PowerShell 5.1. Output (default <repo>\artifacts):
      publish\Kairo\                   complete application folder that is harvested into the MSI
      installer\Kairo-<Version>-x64.msi
      extension\Kairo-Firefox-<Version>.xpi   the extension packed for Firefox/Zen (also installed as
                                             Kairo-Firefox.xpi next to Kairo.exe)

    The MSI is NOT signed here; CI signs it afterwards when a certificate is configured.

.PARAMETER Configuration
    Build configuration (Release or Debug). Default: Release.

.PARAMETER Version
    Product version, numeric major.minor.build (e.g. 1.2.3; major/minor <= 255, build <= 65535).
    Used for the assemblies (Version/FileVersion) and as MSI ProductVersion.

.PARAMETER OutputDir
    Output root. Default: ..\artifacts relative to this script (= <repo>\artifacts).
    A relative path passed explicitly is resolved against the current directory.

.PARAMETER SkipPublish
    Do not run "dotnet publish"; reuse the existing publish folder (e.g. with binaries that were signed
    in between). The extension is still copied and all required files are verified.

.PARAMETER FirefoxXpi
    Use this (Mozilla-signed) XPI instead of packing the extension unsigned. Firefox and Zen only install
    signed add-ons permanently; the release workflow signs via addons.mozilla.org when configured.

.EXAMPLE
    ./installer/build.ps1 -Configuration Release -Version 1.0.42
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Version = '1.0.0',

    [string] $OutputDir = '..\artifacts',

    [switch] $SkipPublish,

    [string] $FirefoxXpi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Runs a native command and fails on a non-zero exit code. $ErrorActionPreference is relaxed while the
# tool runs so that Windows PowerShell 5.1 does not turn stderr output of the tool into a terminating error.
function Invoke-Native([string] $FilePath, [string[]] $Arguments) {
    Write-Host "> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $exitCode."
    }
}

# ------------------------------------------------------------------------------------------------ paths
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

if ($PSBoundParameters.ContainsKey('OutputDir')) {
    $OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
}
else {
    $OutputDir = Join-Path $scriptDir $OutputDir
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$publishDir = Join-Path $OutputDir 'publish\Kairo'
$installerOutDir = Join-Path $OutputDir 'installer'
$appProject = Join-Path $repoRoot 'src\Kairo.App\Kairo.App.csproj'
$hostProject = Join-Path $repoRoot 'src\Kairo.BrowserHost\Kairo.BrowserHost.csproj'
$extensionSource = Join-Path $repoRoot 'extension'
$wixProject = Join-Path $scriptDir 'Kairo.Installer.wixproj'

# ------------------------------------------------------------------------------------------------ checks
$versionMatch = [regex]::Match($Version, '^(\d+)\.(\d+)\.(\d+)$')
if (-not $versionMatch.Success) {
    throw "Version '$Version' is not a valid MSI version. Use major.minor.build, e.g. 1.2.3."
}
if ([int]$versionMatch.Groups[1].Value -gt 255 -or [int]$versionMatch.Groups[2].Value -gt 255 -or [int]$versionMatch.Groups[3].Value -gt 65535) {
    throw "Version '$Version' is out of range for an MSI (major <= 255, minor <= 255, build <= 65535)."
}
$fileVersion = "$Version.0"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (dotnet) was not found in PATH.'
}

Write-Host "Kairo installer build"
Write-Host "  Configuration : $Configuration"
Write-Host "  Version       : $Version (file version $fileVersion)"
Write-Host "  Publish folder: $publishDir"
Write-Host "  MSI folder    : $installerOutDir"

# ------------------------------------------------------------------------------------------------ publish
if ($SkipPublish) {
    Write-Step 'Skipping dotnet publish (-SkipPublish)'
    if (-not (Test-Path -LiteralPath $publishDir)) {
        throw "Publish folder '$publishDir' does not exist. Run without -SkipPublish first."
    }
}
else {
    # Start from an empty folder: the MSI harvests EVERYTHING in it, stale files must not end up in the package.
    if (Test-Path -LiteralPath $publishDir) {
        Write-Step "Cleaning $publishDir"
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    # Both apps go into the SAME folder with the same runtime (Kairo.BrowserHost.exe must live next to Kairo.exe and
    # com.kairo.bridge.json). The host is published first so that Kairo.App's files win if both contain the same file.
    Write-Step 'Publishing Kairo.BrowserHost (self-contained, win-x64)'
    Invoke-Native 'dotnet' @(
        'publish', $hostProject,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        "-p:Version=$Version",
        "-p:FileVersion=$fileVersion",
        '-o', $publishDir,
        '--nologo')

    Write-Step 'Publishing Kairo.App (self-contained, win-x64, ReadyToRun)'
    Invoke-Native 'dotnet' @(
        'publish', $appProject,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        "-p:Version=$Version",
        "-p:FileVersion=$fileVersion",
        '-o', $publishDir,
        '--nologo')
}

# ------------------------------------------------------------------------------------------------ extension
Write-Step 'Copying browser extension (without tools\)'
if (-not (Test-Path -LiteralPath (Join-Path $extensionSource 'manifest.json'))) {
    throw "Extension source '$extensionSource' not found."
}
$extensionTarget = Join-Path $publishDir 'extension'
if (Test-Path -LiteralPath $extensionTarget) {
    Remove-Item -LiteralPath $extensionTarget -Recurse -Force
}
New-Item -ItemType Directory -Path $extensionTarget -Force | Out-Null
Get-ChildItem -LiteralPath $extensionSource -Force |
    Where-Object { $_.Name -ne 'tools' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $extensionTarget -Recurse -Force }

# The published extension carries the product version (Firefox signing requires a new version per upload).
$extensionManifest = Join-Path $extensionTarget 'manifest.json'
$manifestText = [System.IO.File]::ReadAllText($extensionManifest)
$versionPattern = '"version"\s*:\s*"[^"]*"'
if (-not [regex]::IsMatch($manifestText, $versionPattern)) {
    throw 'extension\manifest.json has no "version".'
}
$manifestText = ([regex]$versionPattern).Replace($manifestText, "`"version`": `"$Version`"", 1)
[System.IO.File]::WriteAllText($extensionManifest, $manifestText, (New-Object System.Text.UTF8Encoding($false)))

# ------------------------------------------------------------------------------------------------ Firefox XPI
# An XPI is a ZIP with manifest.json at the root and forward slashes in entry names (ZipFile.CreateFromDirectory
# on .NET Framework writes backslashes, which Firefox rejects).
function New-Xpi([string] $SourceDir, [string] $Destination) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $root = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\') + '\'
    $zip = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $SourceDir -Recurse -File | Sort-Object FullName | ForEach-Object {
            $entryName = $_.FullName.Substring($root.Length).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
}

$extensionOutDir = Join-Path $OutputDir 'extension'
New-Item -ItemType Directory -Path $extensionOutDir -Force | Out-Null
$xpiOut = Join-Path $extensionOutDir "Kairo-Firefox-$Version.xpi"
if ($FirefoxXpi) {
    Write-Step "Using the signed Firefox add-on $FirefoxXpi"
    if (-not (Test-Path -LiteralPath $FirefoxXpi -PathType Leaf)) { throw "FirefoxXpi '$FirefoxXpi' not found." }
    Copy-Item -LiteralPath $FirefoxXpi -Destination $xpiOut -Force
}
else {
    Write-Step 'Packing the extension for Firefox/Zen (unsigned XPI)'
    New-Xpi -SourceDir $extensionTarget -Destination $xpiOut
}
Copy-Item -LiteralPath $xpiOut -Destination (Join-Path $publishDir 'Kairo-Firefox.xpi') -Force

# ------------------------------------------------------------------------------------------------ verify
Write-Step 'Verifying publish folder'
$required = @(
    'Kairo.exe',
    'Kairo.BrowserHost.exe',
    'com.kairo.bridge.json',
    'com.kairo.bridge.firefox.json',
    'Assets\kairo.ico',
    'extension\manifest.json',
    'Kairo-Firefox.xpi'
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishDir $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "Required files are missing in '$publishDir': $($missing -join ', ')"
}
if (Test-Path -LiteralPath (Join-Path $extensionTarget 'tools')) {
    throw "extension\tools must not be part of the package."
}

# The native messaging manifest must point to the host next to it (relative path).
$manifestPath = Join-Path $publishDir 'com.kairo.bridge.json'
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "com.kairo.bridge.json is not valid JSON: $($_.Exception.Message)"
}
$manifestName = [string]($manifest.PSObject.Properties['name'] | ForEach-Object { $_.Value })
$manifestHost = [string]($manifest.PSObject.Properties['path'] | ForEach-Object { $_.Value })
if ($manifestName -ne 'com.kairo.bridge') {
    throw "com.kairo.bridge.json: unexpected name '$manifestName'."
}
if (-not $manifestHost -or [System.IO.Path]::IsPathRooted($manifestHost)) {
    throw "com.kairo.bridge.json: 'path' must be a relative path (is '$manifestHost')."
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDir $manifestHost) -PathType Leaf)) {
    throw "com.kairo.bridge.json: host '$manifestHost' does not exist in the publish folder."
}
$geckoManifest = Get-Content -LiteralPath (Join-Path $publishDir 'com.kairo.bridge.firefox.json') -Raw | ConvertFrom-Json
if ([string]$geckoManifest.path -ne $manifestHost -or @($geckoManifest.allowed_extensions) -notcontains 'kairo-bridge@jevcontrol') {
    throw "com.kairo.bridge.firefox.json must point to '$manifestHost' and allow kairo-bridge@jevcontrol."
}

# The XPI must contain manifest.json at its root with the stamped version.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$xpiZip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $publishDir 'Kairo-Firefox.xpi'))
try {
    $xpiManifestEntry = $xpiZip.GetEntry('manifest.json')
    if (-not $xpiManifestEntry) { throw 'Kairo-Firefox.xpi has no manifest.json at its root.' }
    if (@($xpiZip.Entries | Where-Object { $_.FullName.Contains('\') }).Count -gt 0) { throw 'Kairo-Firefox.xpi contains backslashes in entry names.' }
    $reader = New-Object System.IO.StreamReader($xpiManifestEntry.Open())
    try { $xpiManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ([string]$xpiManifest.version -ne $Version) { throw "Kairo-Firefox.xpi has version '$($xpiManifest.version)', expected $Version." }
}
finally {
    $xpiZip.Dispose()
}

try {
    Get-Content -LiteralPath (Join-Path $extensionTarget 'manifest.json') -Raw | ConvertFrom-Json | Out-Null
}
catch {
    throw "extension\manifest.json is not valid JSON: $($_.Exception.Message)"
}

$publishFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File)
$publishBytes = ($publishFiles | Measure-Object -Property Length -Sum).Sum
Write-Host ("  OK: {0} files, {1:N1} MB" -f $publishFiles.Count, ($publishBytes / 1MB))

# ------------------------------------------------------------------------------------------------ MSI
Write-Step 'Building MSI (WiX Toolset v5)'
New-Item -ItemType Directory -Path $installerOutDir -Force | Out-Null
# PublishDir is passed without a trailing backslash (Windows PowerShell would otherwise break the quoting of
# paths with spaces); the wixproj normalizes it to an absolute directory path with a trailing separator.
Invoke-Native 'dotnet' @(
    'build', $wixProject,
    '-c', $Configuration,
    "-p:ProductVersion=$Version",
    "-p:PublishDir=$($publishDir.TrimEnd('\'))",
    '-o', $installerOutDir,
    '--nologo')

$msiPath = Join-Path $installerOutDir "Kairo-$Version-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) {
    throw "The MSI was not created: $msiPath"
}
$msi = Get-Item -LiteralPath $msiPath

Write-Step 'Done'
Write-Host ("MSI : {0}" -f $msi.FullName) -ForegroundColor Green
Write-Host ("XPI : {0}" -f $xpiOut) -ForegroundColor Green
Write-Host ("Size: {0:N1} MB ({1:N0} bytes)" -f ($msi.Length / 1MB), $msi.Length) -ForegroundColor Green
