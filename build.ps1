<#
.SYNOPSIS
    Builds everything into dist/ in the shape it actually ships.

.DESCRIPTION
    Produces extension\dist (load unpacked) and dist\agent (AutoLang.exe and its runtime).

    This script exists because hand-copying build output broke exactly once, and silently. An
    apphost .exe is a thin launcher and the real code sits in the matching .dll, so copying only
    the .exe left an old build running. Everything looked fine - it started, it answered - it just
    answered with the previous version's data, and the popup showed a confidence threshold of zero.
    Publishing rather than copying makes that class of mistake impossible.

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

# --- Agent ------------------------------------------------------------------------------------

Step 'Agent'
$agentOut = Join-Path $dist 'agent'

# The Agent is resident by design - it outlives the browser and starts itself on demand - so a
# rebuild will nearly always find one holding a lock on its own executable. Stopping it here is
# the normal case, not a workaround.
$running = Get-Process AutoLang -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "stopping $(@($running).Count) running Agent process(es)"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $agentOut) { Remove-Item $agentOut -Recurse -Force }

Invoke-Native dotnet @('publish', (Join-Path $root 'agent\AutoLang.Agent'), '-c', $Configuration, '-o', $agentOut, '--nologo', '-v', 'q') 'Publish failed.'

if (-not (Test-Path (Join-Path $agentOut 'AutoLang.exe'))) {
    throw "AutoLang.exe is missing from $agentOut."
}

# Symbols are useful when debugging and only noise in a release folder.
if ($Configuration -eq 'Release') {
    Get-ChildItem $agentOut -Filter *.pdb | Remove-Item -Force
}

# --- Privacy audit ------------------------------------------------------------------------------

# Runs against the built bundle rather than the source tree, because the bundle is what users run
# and it is generated. Exits non-zero on a failure, so a leak stops the build here rather than
# being discovered by someone reading a privacy policy that is no longer true.
Step 'Privacy audit'
Invoke-Native node @((Join-Path $root 'tools\privacy-audit.mjs')) 'Privacy audit failed. Do not release.'

# --- Extension package ------------------------------------------------------------------------

Step 'Package'

# The zip is what gets uploaded to the Chrome Web Store, and what a user loads unpacked before
# the listing exists.
$zipPath = Join-Path $dist 'extension.zip'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $root 'extension\dist\*') -DestinationPath $zipPath
Write-Host "wrote $zipPath"

# Checks what the store will reject and, more usefully, what it will accept and then break for
# every user. Chief among those is the manifest key: recomputed here into an extension id and
# compared with the id the installer puts in the native host allowlist, because the two are
# constants in two different languages and nothing else notices when they stop agreeing.
Invoke-Native node @((Join-Path $root 'tools\store-preflight.mjs')) 'Store preflight failed. Do not upload this package.'

# Inno Setup is optional. It is not fetched automatically: a build script that silently downloads
# an installer compiler is worse than one that says what is missing.
$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if ($iscc) {
    Invoke-Native $iscc.Source @((Join-Path $root 'installer\AutoLang.iss')) 'Installer build failed.'
} else {
    Write-Host 'skipped installer (Inno Setup not installed; installer\Install.ps1 works today)'
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
       .\native-host\Register-NativeHost.ps1 -BridgePath "$agentOut\AutoLang.exe"
  2. chrome://extensions -> Developer mode -> Load unpacked -> extension\dist
  3. Smoke test the whole chain:
       node tools\bridge-smoke-test.mjs "$agentOut\AutoLang.exe"
"@
