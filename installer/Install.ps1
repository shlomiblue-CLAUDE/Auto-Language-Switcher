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

.EXAMPLE
    .\Install.ps1
    .\Install.ps1 -Source "C:\Downloads\AutoLang"
#>

[CmdletBinding()]
param(
    [string] $Source,
    [switch] $NoAutostart
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

$manifest | ConvertTo-Json -Depth 4 | Out-File -FilePath $manifestPath -Encoding utf8 -Force
Write-Host "wrote    $manifestPath"

foreach ($browser in 'Google\Chrome', 'Microsoft\Edge') {
    $key = "HKCU:\Software\$browser\NativeMessagingHosts\$HostName"
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(Default)' -Value $manifestPath
    Write-Host "wrote    $key"
}

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
