<#
.SYNOPSIS
    Assembles everything another machine needs, as one folder and one zip.

.DESCRIPTION
    Until the extension is in the Chrome Web Store and the Agent has a signed installer, giving the
    product to somebody else means handing them a folder. This builds that folder the same way
    every time, because the first version of it was assembled by hand and it is exactly the kind of
    thing that goes wrong silently: one missing file and the recipient gets "the desktop agent is
    not running" with nothing to tell them why.

    The layout is not arbitrary. Install.ps1 finds the Agent at ..\dist\agent and the verifier at
    ..\tools\verify-registration.ps1, relative to itself, so the package reproduces those two
    paths and nothing else has to be passed in.

    What goes in:

      dist\agent\        the Agent and its runtime, from the last build
      extension\         the unpacked extension, loaded by hand in the browser
      installer\         Install.ps1 and Uninstall.ps1
      tools\             verify-registration.ps1, so -Verify works on the far machine
      קרא-אותי.txt       what to do, and the Defender warning first

    Run build.ps1 first. This copies build output; it does not produce any.

.PARAMETER OutputDir
    Where to put the folder and the zip. Defaults to dist beside the repository.

.EXAMPLE
    .\tools\make-portable.ps1
#>

[CmdletBinding()]
param(
    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }

$package = Join-Path $OutputDir 'AutoLang-portable'
$zip = Join-Path $OutputDir 'AutoLang-portable.zip'

# Named rather than globbed, so a missing one is an error here instead of a support question later.
$sources = @(
    @{ From = 'dist\agent';                      To = 'dist\agent';         Folder = $true }
    @{ From = 'extension\dist';                  To = 'extension';          Folder = $true }
    @{ From = 'installer\Install.ps1';           To = 'installer' }
    @{ From = 'installer\Uninstall.ps1';         To = 'installer' }
    @{ From = 'tools\verify-registration.ps1';   To = 'tools' }
    @{ From = 'installer\read-me.he.txt';        To = '.';                  As = 'קרא-אותי.txt' }
)

foreach ($source in $sources) {
    $full = Join-Path $root $source.From
    if (-not (Test-Path $full)) {
        throw "$($source.From) is missing. Run .\build.ps1 first."
    }
}

# The Agent is the one file whose absence is not obvious from a file count: Defender has deleted it
# mid-build before, and an empty dist\agent still zips perfectly happily.
$agentExe = Join-Path $root 'dist\agent\AutoLang.exe'
if (-not (Test-Path $agentExe)) {
    throw 'dist\agent\AutoLang.exe is missing. If the build reported success, Defender may have quarantined it - see docs\HANDOFF.md.'
}

if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Force $package | Out-Null

foreach ($source in $sources) {
    $from = Join-Path $root $source.From
    $to = if ($source.To -eq '.') { $package } else { Join-Path $package $source.To }
    New-Item -ItemType Directory -Force $to | Out-Null

    if ($source.Folder) {
        Copy-Item (Join-Path $from '*') $to -Recurse -Force
    } elseif ($source.As) {
        Copy-Item $from (Join-Path $to $source.As) -Force
    } else {
        Copy-Item $from $to -Force
    }
}

# Read back rather than trust the copy, for the same reason the check above exists.
$packagedExe = Join-Path $package 'dist\agent\AutoLang.exe'
if (-not (Test-Path $packagedExe)) {
    throw "AutoLang.exe did not survive the copy into $package. Check Windows Security."
}

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal

$files = @(Get-ChildItem $package -Recurse -File)
$size = ($files | Measure-Object Length -Sum).Sum

Write-Host "Packaged for another machine" -ForegroundColor Cyan
Write-Host ("  {0,-10} {1} files, {2:N1} MB" -f 'folder', $files.Count, ($size / 1MB))
Write-Host ("  {0,-10} {1:N1} MB" -f 'zip', ((Get-Item $zip).Length / 1MB))
Write-Host "  $zip"
Write-Host ''
Write-Host 'The recipient runs installer\Install.ps1 -Verify from a PowerShell they opened'
Write-Host 'themselves, then loads the extension folder unpacked. It is all in the read-me.'
Write-Host ''
Write-Host 'Tell them about Defender before they open it, not after.' -ForegroundColor Yellow
