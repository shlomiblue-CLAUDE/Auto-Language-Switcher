<#
.SYNOPSIS
    Registers (or removes) the Auto Language Switcher native messaging host for Chrome and Edge.

.DESCRIPTION
    Chrome finds a native messaging host through a registry value under HKEY_CURRENT_USER that
    points at a manifest file, which in turn names the executable and the extensions allowed to
    talk to it. All three have to agree or the connection fails with an error that names none of
    them, so this script writes all three from one source of truth.

    One executable serves both roles. Chrome launches AutoLang.exe as the host and passes the
    calling extension's origin; seeing that origin is how the process knows to act as the bridge
    rather than as the resident tray agent, and it starts a copy of itself for the agent role.
    The host manifest has nowhere to put arguments - its "path" is an executable and nothing else -
    which is exactly why the role is inferred rather than flagged.

    Everything is per-user (HKCU). No administrator rights are needed and nothing written here
    affects other accounts on the machine.

    The manifest is generated rather than committed, because it must carry an absolute path that
    is only known at install time.

.PARAMETER ExecutablePath
    Full path to AutoLang.exe. Defaults to dist\agent\AutoLang.exe in this repository.

.PARAMETER Uninstall
    Removes the registry entries and the generated manifest.

.EXAMPLE
    .\Register-NativeHost.ps1
    .\Register-NativeHost.ps1 -ExecutablePath "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe"
    .\Register-NativeHost.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [Alias('BridgePath')]
    [string] $ExecutablePath,

    [string] $ExtensionId = 'iblcjhakhfggopgijnankilmifbjbdbp',

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$HostName = 'com.autolang.bridge'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ManifestPath = Join-Path $ScriptDir 'manifests\com.autolang.bridge.json'

$RegistryPaths = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
)

if ($Uninstall) {
    foreach ($path in $RegistryPaths) {
        if (Test-Path $path) {
            Remove-Item $path -Force -Recurse
            Write-Host "removed  $path"
        } else {
            Write-Host "absent   $path"
        }
    }

    if (Test-Path $ManifestPath) {
        Remove-Item $ManifestPath -Force
        Write-Host "removed  $ManifestPath"
    }

    Write-Host "`nUnregistered. Reload the extension for the change to take effect."
    return
}

if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $ScriptDir '..\dist\agent\AutoLang.exe'
}

$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)

if (-not (Test-Path $ExecutablePath)) {
    Write-Error @"
AutoLang.exe was not found at:
  $ExecutablePath

Build it first:
  .\build.ps1
"@
    return
}

$manifest = [ordered]@{
    name            = $HostName
    description     = 'Auto Language Switcher native bridge'
    path            = $ExecutablePath
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

New-Item -ItemType Directory -Force (Split-Path -Parent $ManifestPath) | Out-Null
$manifest | ConvertTo-Json -Depth 4 | Out-File -FilePath $ManifestPath -Encoding utf8 -Force

Write-Host "manifest   $ManifestPath"
Write-Host "executable $ExecutablePath"
Write-Host "origin     chrome-extension://$ExtensionId/`n"

foreach ($path in $RegistryPaths) {
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -Path $path -Name '(Default)' -Value $ManifestPath
    Write-Host "wrote      $path"
}

Write-Host @"

Registered.

Next:
  1. chrome://extensions -> Developer mode -> Load unpacked -> extension\dist
  2. Confirm the extension ID reads $ExtensionId
     (if it does not, the manifest key is missing and the allowlist will reject it)
  3. Open https://web.whatsapp.com
"@
