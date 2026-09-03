<#
.SYNOPSIS
    Installs Auto Language Switcher for the current user.

.DESCRIPTION
    Per-user by design, and not merely to avoid a UAC prompt. Everything this product touches is
    per-user already: the keyboard layout of your session, your conversation preferences, the
    browser's native messaging registry keys. A machine-wide install would need administrator
    rights to manage state that is not machine-wide in the first place.

    What it does:
      1. Copies AutoLang.exe to %LOCALAPPDATA%\Programs\AutoLang
      2. Registers the native messaging host for Chrome and Edge (HKCU)
      3. Starts the Agent at sign-in
      4. Adds an entry to Apps and Features so it can be removed the ordinary way
      5. Starts the Agent now

    Nothing here requires administrator rights, and nothing affects other accounts.

.PARAMETER Source
    Folder holding AutoLang.exe. Defaults to dist\agent beside this repository.

.PARAMETER NoAutostart
    Skip the sign-in entry. The Agent still starts on demand when the browser first needs it.

.PARAMETER Verify
    After installing, confirm from a separate process that the registration is actually visible.

    Worth the extra seconds whenever this script is run by anything other than a person at an
    ordinary PowerShell prompt. A sandboxed or packaged shell can have its registry writes
    virtualised, in which case everything here reports success while the browser sees no
    registration at all. Nothing observable from inside this process distinguishes the two.

.EXAMPLE
    .\Install.ps1
    .\Install.ps1 -Source "C:\Downloads\AutoLang"
    .\Install.ps1 -Verify
#>

[CmdletBinding()]
param(
    [string] $Source,
    [switch] $NoAutostart,
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'

$AppName = 'Auto Language Switcher'
$AppId = 'AutoLang'
$Version = '0.1.0'
$ExtensionId = 'iblcjhakhfggopgijnankilmifbjbdbp'
$HostName = 'com.autolang.bridge'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Source) { $Source = Join-Path $scriptDir '..\dist\agent' }
$Source = [System.IO.Path]::GetFullPath($Source)

$sourceExe = Join-Path $Source 'AutoLang.exe'
if (-not (Test-Path $sourceExe)) {
    throw "AutoLang.exe was not found in $Source. Build it first with .\build.ps1"
}

$installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$targetExe = Join-Path $installDir 'AutoLang.exe'
$manifestPath = Join-Path $installDir "$HostName.json"

Write-Host "Installing $AppName $Version" -ForegroundColor Cyan
Write-Host "  from $Source"
Write-Host "  to   $installDir`n"

# --- Stop anything already running ------------------------------------------------------------

# The Agent is resident and holds its own executable open, so an upgrade over a running copy
# fails on the file copy. Stopping first is the normal upgrade path.
$running = Get-Process AutoLang -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'stopping the running Agent'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# --- Files ------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item $sourceExe $targetExe -Force
Write-Host "copied   $targetExe"

# --- Native messaging host --------------------------------------------------------------------

# The manifest lives beside the executable rather than in the repository, because it has to carry
# an absolute path that is only known now.
$manifest = [ordered]@{
    name            = $HostName
    description     = "$AppName native bridge"
    path            = $targetExe
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

# Written without a byte order mark, deliberately.
#
# Chrome's JSON parser rejects a manifest that starts with one, and then reports the host as
# "Specified native messaging host not found" - an error that points at the registry while the
# actual fault is three bytes at the front of this file. Out-File -Encoding utf8 in Windows
# PowerShell 5.1 writes a BOM, which is exactly how that happened once already.
$json = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding $false))

# Read back and check, because a silent BOM breaks the whole product while every other part of
# the install reports success.
$firstBytes = [System.IO.File]::ReadAllBytes($manifestPath)[0..2]
if ($firstBytes[0] -eq 0xEF -and $firstBytes[1] -eq 0xBB -and $firstBytes[2] -eq 0xBF) {
    throw "The host manifest was written with a byte order mark. Chrome would reject it."
}
Write-Host "wrote    $manifestPath"

# Every Chromium family member reads its own registry path, so registering only for Chrome and
# Edge silently excludes Brave, Vivaldi, Opera, Arc and the beta channels. The failure that
# produces is "Specified native messaging host not found" - which reads as though the registration
# is broken, rather than absent from the one browser being used.
#
# Registering for all of them is per-user, costs nothing, and removes a whole class of support
# question. A browser that is not installed simply never reads its key.
$BrowserKeys = @(
    'Google\Chrome'
    'Google\Chrome Beta'
    'Google\Chrome Dev'
    'Google\Chrome SxS'
    'Chromium'
    'Microsoft\Edge'
    'Microsoft\Edge Beta'
    'Microsoft\Edge Dev'
    'BraveSoftware\Brave-Browser'
    'BraveSoftware\Brave-Browser-Beta'
    'Vivaldi'
    'Opera Software\Opera Stable'
    'Opera Software\Opera GX Stable'
    'ArcBrowser\Arc'
)

foreach ($browser in $BrowserKeys) {
    $key = "HKCU:\Software\$browser\NativeMessagingHosts\$HostName"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(Default)' -Value $manifestPath
}
Write-Host ("wrote    native messaging registration for {0} browsers" -f $BrowserKeys.Count)

# --- Start at sign-in -------------------------------------------------------------------------

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($NoAutostart) {
    Remove-ItemProperty -Path $runKey -Name $AppId -ErrorAction SilentlyContinue
    Write-Host 'skipped  autostart'
} else {
    Set-ItemProperty -Path $runKey -Name $AppId -Value "`"$targetExe`""
    Write-Host "wrote    $runKey\$AppId"
}

# --- Apps and Features ------------------------------------------------------------------------

# Without this the product is a folder and some registry keys that a user has no ordinary way to
# remove. Anything that installs itself should be uninstallable the same way everything else is.
$uninstallScript = Join-Path $installDir 'Uninstall.ps1'
Copy-Item (Join-Path $scriptDir 'Uninstall.ps1') $uninstallScript -Force

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
New-Item -Path $uninstallKey -Force | Out-Null

$values = @{
    DisplayName     = $AppName
    DisplayVersion  = $Version
    Publisher       = $AppName
    InstallLocation = $installDir
    DisplayIcon     = $targetExe
    NoModify        = 1
    NoRepair        = 1
    EstimatedSize   = [int]((Get-Item $targetExe).Length / 1KB)
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
}

foreach ($name in $values.Keys) {
    Set-ItemProperty -Path $uninstallKey -Name $name -Value $values[$name]
}
Write-Host "wrote    $uninstallKey"

# --- Start it ---------------------------------------------------------------------------------

Start-Process $targetExe
Start-Sleep -Milliseconds 800

$started = Get-Process AutoLang -ErrorAction SilentlyContinue
Write-Host ("started  {0}" -f $(if ($started) { "Agent (pid $($started.Id))" } else { 'FAILED - the Agent did not stay running' }))

# --- Was any of that real? ---------------------------------------------------------------------

if ($Verify) {
    Write-Host ''
    $verifier = Join-Path $scriptDir '..\tools\verify-registration.ps1'
    if (Test-Path $verifier) {
        & $verifier -HostName $HostName -ExtensionId $ExtensionId
        # 2 means the check could not be performed, which is not evidence of a broken install.
        if ($LASTEXITCODE -eq 1) {
            throw 'The registration is not visible outside this process. The install did not take effect.'
        }
    } else {
        Write-Host "skipped  verification (tools\verify-registration.ps1 not found beside the installer)"
    }
}

Write-Host @"

Installed.

One step left, and it cannot be automated: the browser extension has to be added by hand
until it is published to the Chrome Web Store.

  1. Open chrome://extensions (or edge://extensions)
  2. Turn on Developer mode
  3. Load unpacked, and choose the extension\dist folder
  4. Check the extension ID reads $ExtensionId

     If it does not, the extension was built without its signing key and the native host will
     refuse it - the allowlist names that exact id.

  5. Open https://web.whatsapp.com

To remove: Settings > Apps > Auto Language Switcher, or run
  $uninstallScript
"@
