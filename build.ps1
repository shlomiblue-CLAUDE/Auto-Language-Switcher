<#
.SYNOPSIS
    Builds everything into dist/ in the shape it actually ships.

.DESCRIPTION
    The Agent and the Bridge MUST land in one folder. The Bridge starts the Agent from its own
    directory, so a split layout means the extension reports AGENT_UNAVAILABLE and nothing works.

    This script exists because hand-copying between bin folders broke exactly once, and silently:
    AutoLangAgent.exe is a thin apphost and the real code is in AutoLangAgent.dll, so copying only
    the .exe left an old build running. Everything looked fine - it started, it answered - it just
    answered with the previous version's data. Publishing both projects to one folder makes that
    class of mistake impossible.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER SkipTests
    Skip the test suites. Not recommended.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug -SkipTests
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# Both toolchains are per-user or freshly installed, so a shell started before installation has a
# stale PATH and neither is found. Resolving them here means the script works from any terminal
# rather than only from one that happens to have been opened at the right moment.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $dotnetRoot = "$env:LOCALAPPDATA\Microsoft\dotnet"
    if (Test-Path "$dotnetRoot\dotnet.exe") {
        $env:DOTNET_ROOT = $dotnetRoot
        $env:PATH = "$dotnetRoot;$env:PATH"
    } else {
        throw 'dotnet was not found. Install the .NET 8 SDK.'
    }
}

if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    # A per-user .NET install needs DOTNET_ROOT or apphost-built exes fail with hostfxr not found.
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
}

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    $nodeDir = @("$env:ProgramFiles\nodejs", "${env:ProgramFiles(x86)}\nodejs", "$env:LOCALAPPDATA\Programs\nodejs") |
        Where-Object { Test-Path (Join-Path $_ 'npm.cmd') } |
        Select-Object -First 1

    if ($nodeDir) { $env:PATH = "$nodeDir;$env:PATH" }
    else { throw 'npm was not found. Install Node.js LTS.' }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step($message) { Write-Host "`n=== $message ===" -ForegroundColor Cyan }

<#
    Runs a native command and judges it by its exit code alone.

    Windows PowerShell 5.1 turns anything a native executable writes to stderr into an ErrorRecord,
    and with $ErrorActionPreference = 'Stop' that aborts the script. esbuild prints its perfectly
    ordinary build summary to stderr, so a successful build was being reported as a failure. Exit
    code is the only signal that actually means what it says.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)] [string] $Command,
        [string[]] $Arguments = @(),
        [string] $FailureMessage
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command @Arguments
    } finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE -ne 0) {
        if ($FailureMessage) { throw $FailureMessage }
        throw "$Command exited with $LASTEXITCODE."
    }
}

# --- Tests ------------------------------------------------------------------------------------

if (-not $SkipTests) {
    Step 'C# tests'
    Invoke-Native dotnet @('test', $root, '--nologo', '-v', 'q') 'C# tests failed.'

    Step 'TypeScript tests'
    Push-Location (Join-Path $root 'extension')
    try {
        Invoke-Native npm @('test') 'TypeScript tests failed.'
        Invoke-Native npx @('tsc', '--noEmit') 'TypeScript typecheck failed.'
    } finally { Pop-Location }
}

# --- Icons ------------------------------------------------------------------------------------

Step 'Icons'
Invoke-Native node @((Join-Path $root 'tools\make-icons.mjs')) 'Icon generation failed.'

# --- Extension --------------------------------------------------------------------------------

Step 'Extension'
Push-Location (Join-Path $root 'extension')
try {
    Invoke-Native npm @('run', 'build') 'Extension build failed.'
} finally { Pop-Location }

# --- Agent and Bridge, into ONE folder --------------------------------------------------------

Step 'Agent and Bridge'
$agentOut = Join-Path $dist 'agent'

# The Agent is resident by design - it survives the browser and starts itself on demand - so a
# rebuild will nearly always find one holding a lock on its own executable. Stopping it here is
# not a workaround; it is the normal case.
$running = Get-Process AutoLangAgent -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping $($running.Count) running Agent process(es)"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $agentOut) { Remove-Item $agentOut -Recurse -Force }

foreach ($project in 'agent\AutoLang.Agent', 'agent\AutoLang.Bridge') {
    Invoke-Native dotnet @('publish', (Join-Path $root $project), '-c', $Configuration, '-o', $agentOut, '--nologo', '-v', 'q') "Publish failed for $project."
}

foreach ($required in 'AutoLangAgent.exe', 'AutoLangBridge.exe') {
    if (-not (Test-Path (Join-Path $agentOut $required))) {
        throw "$required is missing from $agentOut. The Bridge cannot start the Agent without it."
    }
}

# Symbols are useful when debugging and only noise in a release folder.
if ($Configuration -eq 'Release') {
    Get-ChildItem $agentOut -Filter *.pdb | Remove-Item -Force
}

# --- Summary ----------------------------------------------------------------------------------

Step 'Built'

$extensionSize = (Get-ChildItem (Join-Path $root 'extension\dist') -Recurse -File | Measure-Object Length -Sum).Sum
$agentSize = (Get-ChildItem $agentOut -Recurse -File | Measure-Object Length -Sum).Sum

Write-Host ("extension  {0,8:N0} KB  extension\dist" -f ($extensionSize / 1KB))
Write-Host ("agent      {0,8:N0} KB  dist\agent" -f ($agentSize / 1KB))

Write-Host @"

Next:
  1. Register the native host against this build:
       .\native-host\Register-NativeHost.ps1 -BridgePath "$agentOut\AutoLangBridge.exe"
  2. chrome://extensions -> Developer mode -> Load unpacked -> extension\dist
  3. Smoke test the whole chain:
       node tools\bridge-smoke-test.mjs "$agentOut\AutoLangBridge.exe"
"@
