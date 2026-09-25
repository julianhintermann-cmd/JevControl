<#
.SYNOPSIS
    Installs the Kairo MSI silently (per user), checks the installation, runs the self test,
    uninstalls silently without REMOVEUSERDATA (user data must be kept), reinstalls, uninstalls with
    REMOVEUSERDATA=1 and checks that everything including the user data is gone. For CI.

.DESCRIPTION
    Runs on PowerShell 7 and Windows PowerShell 5.1, without admin rights (per-user MSI).
    Exit code 0 = all assertions passed, 1 = at least one assertion failed.
    msiexec logs and selftest.json are written to -LogDir; on failure the end of the msiexec logs is printed.

.PARAMETER Msi
    Path to Kairo-<version>-x64.msi. Default: newest *.msi in <repo>\artifacts\installer.

.PARAMETER LogDir
    Folder for install.log, uninstall.log and selftest.json. Default: <repo>\artifacts\test-install.

.PARAMETER SelfTestTimeoutSeconds
    Maximum run time of "Kairo.exe --selftest". Default: 180.

.PARAMETER AllowUserDataRemoval
    Outside CI the script refuses to run when %APPDATA%\Kairo or %LOCALAPPDATA%\Kairo already exist, because
    the final uninstall deletes them. Pass this switch to run it anyway.

.EXAMPLE
    ./installer/test-install.ps1 -Msi artifacts/installer/Kairo-1.0.0-x64.msi
#>
[CmdletBinding()]
param(
    [Alias('MsiPath')]
    [string] $Msi,

    [string] $LogDir,

    [int] $SelfTestTimeoutSeconds = 180,

    # The test creates and finally deletes %APPDATA%\Kairo and %LOCALAPPDATA%\Kairo. Outside CI it refuses to
    # run when these folders already exist (real settings, API key, history) unless this switch is given.
    [switch] $AllowUserDataRemoval
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$isCi = $env:GITHUB_ACTIONS -eq 'true'

$script:failures = New-Object System.Collections.Generic.List[string]

function Write-Section([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Pass([string] $Message) {
    Write-Host "  [OK]   $Message" -ForegroundColor Green
}

function Fail([string] $Message) {
    $script:failures.Add($Message)
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
    if ($isCi) { Write-Host "::error title=Kairo installer test::$Message" }
}

function Warn([string] $Message) {
    Write-Host "  [WARN] $Message" -ForegroundColor Yellow
    if ($isCi) { Write-Host "::warning title=Kairo installer test::$Message" }
}

function Assert-True([bool] $Condition, [string] $Message) {
    if ($Condition) { Pass $Message } else { Fail $Message }
}

function Get-DefaultValue([string] $RegistryPath) {
    # Default value "(Default)" of an HKCU key, or $null.
    if (-not (Test-Path -LiteralPath $RegistryPath)) { return $null }
    return (Get-Item -LiteralPath $RegistryPath).GetValue('')
}

function Get-RegistryValue([string] $RegistryPath, [string] $Name) {
    if (-not (Test-Path -LiteralPath $RegistryPath)) { return $null }
    return (Get-Item -LiteralPath $RegistryPath).GetValue($Name)
}

function Get-ShortcutTarget([string] $LinkPath) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        return $shell.CreateShortcut($LinkPath).TargetPath
    }
    catch {
        return $null
    }
}

function Test-SamePath([string] $A, [string] $B) {
    if (-not $A -or -not $B) { return $false }
    return [string]::Equals([System.IO.Path]::GetFullPath($A).TrimEnd('\'), [System.IO.Path]::GetFullPath($B).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

# Reads a property from the MSI's Property table via the Windows Installer automation interface.
function Get-MsiProperty([string] $Path, [string] $Name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
    $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'"))
    try {
        [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { return $null }
        return $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
    }
    finally {
        [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($db)
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
    }
}

# 5 = installed, -1 = unknown product.
function Get-ProductState([string] $ProductCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        return [int]$installer.GetType().InvokeMember('ProductState', 'GetProperty', $null, $installer, @($ProductCode))
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Invoke-Msiexec([string] $Arguments) {
    Write-Host "> msiexec.exe $Arguments" -ForegroundColor DarkGray
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -Wait -PassThru
    return $process.ExitCode
}

function Show-LogTail([string] $Path, [int] $Lines = 80) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "  (log '$Path' does not exist)"
        return
    }
    $firstError = Select-String -LiteralPath $Path -Pattern 'Return value 3' -SimpleMatch | Select-Object -First 1
    if ($firstError) {
        Write-Host ''
        Write-Host "---- $Path : context of the first 'Return value 3' (line $($firstError.LineNumber)) ----" -ForegroundColor Yellow
        $all = @(Get-Content -LiteralPath $Path)
        $from = [Math]::Max(0, $firstError.LineNumber - 40)
        $to = [Math]::Min($all.Count - 1, $firstError.LineNumber + 2)
        $all[$from..$to] | ForEach-Object { Write-Host $_ }
    }
    Write-Host ''
    Write-Host "---- $Path : last $Lines lines ----" -ForegroundColor Yellow
    Get-Content -LiteralPath $Path -Tail $Lines | ForEach-Object { Write-Host $_ }
}

# ------------------------------------------------------------------------------------------------ setup
if (-not $Msi) {
    $candidate = Get-ChildItem -Path (Join-Path $repoRoot 'artifacts\installer') -Filter '*.msi' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'No MSI given (-Msi) and none found in artifacts\installer.' }
    $Msi = $candidate.FullName
}
$Msi = (Resolve-Path -LiteralPath $Msi).ProviderPath
if (-not $LogDir) { $LogDir = Join-Path $repoRoot 'artifacts\test-install' }
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$LogDir = (Resolve-Path -LiteralPath $LogDir).ProviderPath

$installLog = Join-Path $LogDir 'install.log'
$uninstallLog = Join-Path $LogDir 'uninstall.log'
$selfTestJson = Join-Path $LogDir 'selftest.json'
Remove-Item -LiteralPath $installLog, $uninstallLog, $selfTestJson -Force -ErrorAction SilentlyContinue

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Kairo'
$kairoExe = Join-Path $installDir 'Kairo.exe'
$startMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kairo.lnk'   # %APPDATA%\Microsoft\Windows\Start Menu\Programs
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Kairo.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$installerKey = 'HKCU:\Software\Kairo\Installer'
$nativeHostKeys = [ordered]@{
    'Chrome' = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.kairo.bridge'
    'Edge'   = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.kairo.bridge'
}
$extensionOrigin = 'chrome-extension://fjdcafkellelfdkneebdlmoggkhkilmh/'

Write-Host "MSI      : $Msi"
Write-Host "Logs     : $LogDir"
Write-Host "Expected : $installDir"

$productCode = $null
try {
    $productCode = Get-MsiProperty -Path $Msi -Name 'ProductCode'
    $productVersion = Get-MsiProperty -Path $Msi -Name 'ProductVersion'
    Write-Host "Product  : $productCode, version $productVersion"
}
catch {
    Warn "Could not read the MSI properties via WindowsInstaller.Installer: $($_.Exception.Message)"
}

if (Test-Path -LiteralPath $installDir) {
    Warn "The install folder already exists before the test: $installDir"
}

$dataDirs = @((Join-Path $env:APPDATA 'Kairo'), (Join-Path $env:LOCALAPPDATA 'Kairo'))
$existingData = @($dataDirs | Where-Object { Test-Path -LiteralPath $_ })
if ($existingData.Count -gt 0 -and -not $isCi -and -not $AllowUserDataRemoval) {
    throw "Kairo user data exists on this machine ($($existingData -join ', ')). The test deletes it at the end; run it on a test machine or pass -AllowUserDataRemoval."
}

# ------------------------------------------------------------------------------------------------ install
Write-Section 'Installing (silent, per user, INSTALLDESKTOPSHORTCUT=1 AUTOSTART=1)'
$exitCode = Invoke-Msiexec "/i `"$Msi`" /qn /l*v `"$installLog`" INSTALLDESKTOPSHORTCUT=1 AUTOSTART=1"
if ($exitCode -eq 3010) {
    Warn 'msiexec /i returned 3010 (reboot required).'
}
elseif ($exitCode -ne 0) {
    Fail "msiexec /i failed with exit code $exitCode."
    Show-LogTail $installLog
    exit 1
}
Pass "msiexec /i finished (exit code $exitCode)"

# ------------------------------------------------------------------------------------------------ check install
Write-Section 'Checking the installation'
foreach ($relative in @('Kairo.exe', 'Kairo.BrowserHost.exe', 'com.kairo.bridge.json', 'Assets\kairo.ico', 'extension\manifest.json')) {
    Assert-True (Test-Path -LiteralPath (Join-Path $installDir $relative) -PathType Leaf) "File installed: $relative"
}
Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDir 'extension\tools'))) 'extension\tools is not installed'

Assert-True (Test-Path -LiteralPath $startMenuLink -PathType Leaf) "Start menu shortcut exists: $startMenuLink"
if (Test-Path -LiteralPath $startMenuLink) {
    $target = Get-ShortcutTarget $startMenuLink
    Assert-True (Test-SamePath $target $kairoExe) "Start menu shortcut points to Kairo.exe (is '$target')"
}
Assert-True (Test-Path -LiteralPath $desktopLink -PathType Leaf) "Desktop shortcut exists: $desktopLink"
if (Test-Path -LiteralPath $desktopLink) {
    $target = Get-ShortcutTarget $desktopLink
    Assert-True (Test-SamePath $target $kairoExe) "Desktop shortcut points to Kairo.exe (is '$target')"
}

$runValue = Get-RegistryValue $runKey 'Kairo'
$expectedRun = "`"$kairoExe`" --background"
Assert-True ([string]::Equals([string]$runValue, $expectedRun, [StringComparison]::OrdinalIgnoreCase)) "Autostart value HKCU\...\Run\Kairo = $expectedRun (is '$runValue')"

$rememberedFolder = Get-RegistryValue $installerKey 'InstallFolder'
Assert-True (Test-SamePath ([string]$rememberedFolder) $installDir) "HKCU\Software\Kairo\Installer\InstallFolder = $installDir\ (is '$rememberedFolder')"

foreach ($browser in $nativeHostKeys.Keys) {
    $key = $nativeHostKeys[$browser]
    $manifestPath = [string](Get-DefaultValue $key)
    if (-not $manifestPath) {
        Fail "$browser native messaging host is registered ($key)"
        continue
    }
    Pass "$browser native messaging host is registered -> $manifestPath"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Fail "$browser manifest exists: $manifestPath"
        continue
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $hostPath = [string]($manifest.PSObject.Properties['path'] | ForEach-Object { $_.Value })
        if ($hostPath -and -not [System.IO.Path]::IsPathRooted($hostPath)) {
            $hostPath = Join-Path (Split-Path -Parent $manifestPath) $hostPath
        }
        $origins = @($manifest.PSObject.Properties['allowed_origins'] | ForEach-Object { $_.Value })
        Assert-True ([string]($manifest.PSObject.Properties['name'] | ForEach-Object { $_.Value }) -eq 'com.kairo.bridge') "$browser manifest name is com.kairo.bridge"
        Assert-True ($hostPath -and (Test-Path -LiteralPath $hostPath -PathType Leaf)) "$browser manifest path resolves to an existing host exe ($hostPath)"
        Assert-True ($origins -contains $extensionOrigin) "$browser manifest allows $extensionOrigin"
    }
    catch {
        Fail "$browser manifest is valid JSON ($($_.Exception.Message))"
    }
}

if ($productCode) {
    try {
        $state = Get-ProductState $productCode
        Assert-True ($state -eq 5) "Windows Installer reports the product as installed (state $state)"
    }
    catch {
        Warn "Could not query the product state: $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------------------------------------------ self test
Write-Section 'Running Kairo.exe --selftest'
if (Test-Path -LiteralPath $kairoExe) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $kairoExe
    $psi.Arguments = "--selftest --selftest-out `"$selfTestJson`""
    $psi.WorkingDirectory = $LogDir
    $psi.UseShellExecute = $false
    $selfTest = [System.Diagnostics.Process]::Start($psi)
    if ($selfTest.WaitForExit($SelfTestTimeoutSeconds * 1000)) {
        $selfTest.WaitForExit()
        Assert-True ($selfTest.ExitCode -eq 0) "Kairo.exe --selftest exit code is 0 (is $($selfTest.ExitCode))"
    }
    else {
        try { $selfTest.Kill() } catch { }
        Fail "Kairo.exe --selftest did not finish within $SelfTestTimeoutSeconds s"
    }
    if (Test-Path -LiteralPath $selfTestJson) {
        Write-Host "---- $selfTestJson ----"
        Get-Content -LiteralPath $selfTestJson | ForEach-Object { Write-Host $_ }
    }
    else {
        Fail "Self test report was written: $selfTestJson"
    }
}
else {
    Fail 'Kairo.exe --selftest could not run (Kairo.exe missing)'
}

$running = @(Get-Process -Name 'Kairo', 'Kairo.BrowserHost' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Warn "Kairo processes are still running before uninstall (the MSI closes them): $(($running | ForEach-Object { $_.Id }) -join ', ')"
}

# ------------------------------------------------------------------------------------------------ uninstall, keep data
# The user chooses between keeping and securely removing settings, history and API key. First: keep.
Write-Section 'Uninstalling without REMOVEUSERDATA (user data must be kept)'
$settingsMarker = Join-Path $dataDirs[0] 'settings.json'
$localMarker = Join-Path $dataDirs[1] 'logs\installer-test.log'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $settingsMarker), (Split-Path -Parent $localMarker) | Out-Null
Set-Content -LiteralPath $settingsMarker -Value '{ "installerTest": true }' -Encoding UTF8
Set-Content -LiteralPath $localMarker -Value 'installer test' -Encoding UTF8

$keepLog = Join-Path $LogDir 'uninstall-keep.log'
$keepExit = Invoke-Msiexec "/x `"$Msi`" /qn /l*v `"$keepLog`""
Assert-True ($keepExit -eq 0 -or $keepExit -eq 3010) "msiexec /x (keep data) finished (exit code $keepExit)"
Assert-True (-not (Test-Path -LiteralPath $kairoExe)) 'Kairo.exe removed'
Assert-True ($null -eq (Get-RegistryValue $runKey 'Kairo')) 'Autostart value removed'
Assert-True ((Test-Path -LiteralPath $settingsMarker) -and (Test-Path -LiteralPath $localMarker)) 'User data kept (no REMOVEUSERDATA)'

Write-Section 'Reinstalling (user data from the previous installation is still available)'
$reinstallLog = Join-Path $LogDir 'reinstall.log'
$reinstallExit = Invoke-Msiexec "/i `"$Msi`" /qn /l*v `"$reinstallLog`" INSTALLDESKTOPSHORTCUT=1 AUTOSTART=1"
Assert-True ($reinstallExit -eq 0 -or $reinstallExit -eq 3010) "msiexec /i (reinstall) finished (exit code $reinstallExit)"
Assert-True (Test-Path -LiteralPath $kairoExe -PathType Leaf) 'Kairo.exe installed again'
Assert-True (Test-Path -LiteralPath $settingsMarker) 'User settings survived uninstall and reinstall'

# ------------------------------------------------------------------------------------------------ uninstall, remove data
Write-Section 'Uninstalling (silent, REMOVEUSERDATA=1)'
$uninstallExit = Invoke-Msiexec "/x `"$Msi`" /qn /l*v `"$uninstallLog`" REMOVEUSERDATA=1"
if ($uninstallExit -eq 3010) {
    Warn 'msiexec /x returned 3010 (reboot required).'
}
else {
    Assert-True ($uninstallExit -eq 0) "msiexec /x finished (exit code $uninstallExit)"
}

# ------------------------------------------------------------------------------------------------ check uninstall
Write-Section 'Checking that Kairo was removed'
$folderGone = -not (Test-Path -LiteralPath $installDir)
Assert-True $folderGone "Install folder removed: $installDir"
if (-not $folderGone) {
    Write-Host '  Remaining content:'
    Get-ChildItem -LiteralPath $installDir -Recurse -Force | ForEach-Object { Write-Host "    $($_.FullName)" }
}
Assert-True (-not (Test-Path -LiteralPath $startMenuLink)) 'Start menu shortcut removed'
Assert-True (-not (Test-Path -LiteralPath $desktopLink)) 'Desktop shortcut removed'
Assert-True ($null -eq (Get-RegistryValue $runKey 'Kairo')) 'Autostart value HKCU\...\Run\Kairo removed'
foreach ($browser in $nativeHostKeys.Keys) {
    Assert-True (-not (Test-Path -LiteralPath $nativeHostKeys[$browser])) "$browser native messaging host key removed"
}
Assert-True (-not (Test-Path -LiteralPath $installerKey)) 'HKCU\Software\Kairo\Installer removed'

if ($productCode) {
    try {
        $state = Get-ProductState $productCode
        Assert-True ($state -ne 5) "Windows Installer no longer reports the product as installed (state $state)"
    }
    catch {
        Warn "Could not query the product state: $($_.Exception.Message)"
    }
}

# User data is removed by "Kairo.exe --uninstall-cleanup --remove-data" (custom action; the MSI ignores its errors,
# so the result is checked here).
foreach ($dataDir in $dataDirs) {
    Assert-True (-not (Test-Path -LiteralPath $dataDir)) "User data securely removed with REMOVEUSERDATA=1: $dataDir"
}

# ------------------------------------------------------------------------------------------------ result
Write-Host ''
if ($script:failures.Count -gt 0) {
    Write-Host "$($script:failures.Count) check(s) failed:" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Show-LogTail $installLog
    Show-LogTail $keepLog
    Show-LogTail $reinstallLog
    Show-LogTail $uninstallLog
    exit 1
}

Write-Host 'All installer checks passed.' -ForegroundColor Green
exit 0
