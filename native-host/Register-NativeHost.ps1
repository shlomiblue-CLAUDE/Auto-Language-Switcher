<#
.SYNOPSIS
    Registers (or removes) the Auto Language Switcher native messaging host for Chrome and Edge.

.DESCRIPTION
    Chrome finds a native messaging host through a registry value under HKEY_CURRENT_USER that
    points at a manifest file, which in turn names the executable and the extensions allowed to
    talk to it. All three have to agree or the connection fails with an error that names none of
    them, so this script writes all three from one source of truth.

    Everything is per-user (HKCU). No administrator rights are needed, and nothing is written that
    affects other accounts on the machine.

    The manifest is generated rather than committed, because it has to carry an absolute path that
    is only known at install time.

.PARAMETER BridgePath
    Full path to AutoLangBridge.exe. Defaults to the debug build beside this script.

.PARAMETER Uninstall
    Removes the registry entries and the generated manifest.

.EXAMPLE
    .\Register-NativeHost.ps1
    .\Register-NativeHost.ps1 -BridgePath "C:\Program Files\AutoLang\AutoLangBridge.exe"
    .\Register-NativeHost.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [string] $BridgePath,
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

if (-not $BridgePath) {
    $BridgePath = Join-Path $ScriptDir '..\agent\AutoLang.Bridge\bin\Debug\net8.0-windows\win-x64\AutoLangBridge.exe'
}

$BridgePath = [System.IO.Path]::GetFullPath($BridgePath)

if (-not (Test-Path $BridgePath)) {
    Write-Error @"
Bridge executable not found at:
  $BridgePath

Build it first:
  dotnet build agent/AutoLang.Bridge
"@
    return
}

# The Agent has to sit beside the Bridge; that is how the Bridge finds it without configuration.
$AgentPath = Join-Path (Split-Path -Parent $BridgePath) 'AutoLangAgent.exe'
if (-not (Test-Path $AgentPath)) {
    Write-Warning @"
AutoLangAgent.exe is not next to the Bridge at:
  $AgentPath

The Bridge starts the Agent from its own directory. Without it, the extension will report
AGENT_UNAVAILABLE. Copy the Agent build beside the Bridge, or publish both to one folder.
"@
}

$manifest = [ordered]@{
    name            = $HostName
    description     = 'Auto Language Switcher native bridge'
    path            = $BridgePath
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}

New-Item -ItemType Directory -Force (Split-Path -Parent $ManifestPath) | Out-Null
$manifest | ConvertTo-Json -Depth 4 | Out-File -FilePath $ManifestPath -Encoding utf8 -Force

Write-Host "manifest $ManifestPath"
Write-Host "bridge   $BridgePath"
Write-Host "origin   chrome-extension://$ExtensionId/`n"

foreach ($path in $RegistryPaths) {
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -Path $path -Name '(Default)' -Value $ManifestPath
    Write-Host "wrote    $path"
}

Write-Host @"

Registered.

Next:
  1. chrome://extensions -> Developer mode -> Load unpacked -> extension\dist
  2. Confirm the extension ID reads $ExtensionId
     (if it does not, the manifest key is missing and the allowlist will reject it)
  3. Open https://web.whatsapp.com
"@
