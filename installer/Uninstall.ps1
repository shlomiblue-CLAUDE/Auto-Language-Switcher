<#
.SYNOPSIS
    Removes Auto Language Switcher for the current user.

.DESCRIPTION
    Reverses everything Install.ps1 did: the executable, the native messaging registration for both
    browsers, the Startup shortcut, and the Apps and Features record.

    Your stored conversation preferences are kept unless you pass -RemoveData. Uninstalling is
    often a reinstall, and silently discarding which language someone uses in each of their
    conversations is not a decision an uninstaller should make on their behalf.

    The browser extension has to be removed separately, from chrome://extensions. Nothing outside
    the browser can remove it.

.PARAMETER RemoveData
    Also delete %LOCALAPPDATA%\AutoLang, which holds settings, per-conversation preferences and
    the install salt. This cannot be undone.

.EXAMPLE
    .\Uninstall.ps1
    .\Uninstall.ps1 -RemoveData
#>

[CmdletBinding()]
param(
    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'

$AppId = 'AutoLang'
$HostName = 'com.autolang.bridge'

$installDir = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$dataDir = Join-Path $env:LOCALAPPDATA $AppId

Write-Host 'Removing Auto Language Switcher' -ForegroundColor Cyan

# --- Stop the Agent ---------------------------------------------------------------------------

$running = Get-Process AutoLang -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host 'stopped  the Agent'
}

# --- Registry ---------------------------------------------------------------------------------

foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
    $key = "HKCU:\Software\$browser\NativeMessagingHosts\$HostName"
    if (Test-Path $key) {
        Remove-Item $key -Force -Recurse
        Write-Host "removed  $key"
    }
}

# Autostart moved to a Startup shortcut after Defender quarantined the executable over the Run
# value; see the note in Install.ps1. Both are removed, because an uninstall that leaves a stale
# Run value pointing at a deleted file leaves the machine with a broken sign-in entry and a
# heuristic's favourite artefact.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name $AppId -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name $AppId
    Write-Host "removed  $runKey\$AppId"
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Auto Language Switcher.lnk'
if (Test-Path $shortcut) {
    Remove-Item $shortcut -Force
    Write-Host "removed  $shortcut"
}

$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
if (Test-Path $uninstallKey) {
    Remove-Item $uninstallKey -Force -Recurse
    Write-Host "removed  $uninstallKey"
}

# --- Files ------------------------------------------------------------------------------------

if (Test-Path $installDir) {
    # This script is copied into the install folder and is running from it, so the folder cannot
    # simply be deleted underneath itself. Everything else goes now; the script and its folder are
    # handed to a detached cmd that waits for this process to exit first.
    Get-ChildItem $installDir -Exclude 'Uninstall.ps1' | Remove-Item -Recurse -Force
    Write-Host "removed  $installDir contents"

    $self = Join-Path $installDir 'Uninstall.ps1'
    if (Test-Path $self) {
        Start-Process cmd.exe -ArgumentList '/c', 'timeout', '/t', '3', '/nobreak', '>nul', '&', 'rmdir', '/s', '/q', "`"$installDir`"" `
            -WindowStyle Hidden
    }
}

# --- Data -------------------------------------------------------------------------------------

if ($RemoveData) {
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
        Write-Host "removed  $dataDir"
    }
} elseif (Test-Path $dataDir) {
    Write-Host "kept     $dataDir (run with -RemoveData to delete it)"
}

Write-Host @"

Removed.

The browser extension has to be removed separately: open chrome://extensions (or
edge://extensions) and remove Auto Language Switcher there.
"@
