<#
.SYNOPSIS
    Checks that the native messaging registration is visible to the browser.

.DESCRIPTION
    Reads the registration the way a browser reads it: from a process that is not this one.

    This exists because of a failure that consumed an entire debugging session. Registry writes can
    be virtualised - by an application sandbox, by App-V, by a packaged (MSIX) host, by some VDI
    profile layers - so that the writing process reads its own value straight back while every other
    process on the machine, the browser included, sees nothing at that path at all.

    Every check available from inside the installing process passed: Install.ps1 reported success,
    `reg query` from the same shell printed the key and its default value, the manifest parsed as
    JSON and carried no byte order mark, the executable existed, no enterprise policy was present.
    Chrome still answered "Specified native messaging host not found" - because Chrome was reading
    the real hive, and the key had never been written there.

    There is no way to detect that from inside. GetCurrentPackageFullName reports no package
    identity, and reading through HKEY_USERS\<SID> instead of HKCU returns the same virtualised
    view. The only question that distinguishes the two cases is whether a DIFFERENT process can see
    the value, so that is the question this script asks.

    The out-of-process reader is started through WMI rather than as a child process, because a child
    inherits the redirection and would answer the same way this shell does.

    Not part of Install.ps1 by default. Spawning a process through WMI is a recognised execution
    technique and a consumer installer that does it on every run earns attention from endpoint
    security for no benefit to the ordinary user, who has no redirection to detect. Run it when the
    extension reports a missing host, and always after an automated or sandboxed install:
    `.\Install.ps1 -Verify` does exactly that.

.PARAMETER HostName
    Native messaging host name. Defaults to the one this product registers.

.PARAMETER ExtensionId
    Extension id expected in the manifest's allowed_origins.

.PARAMETER TimeoutSeconds
    How long to wait for the out-of-process reader. It is a cmd.exe and two reg queries.

.EXAMPLE
    .\verify-registration.ps1
#>

[CmdletBinding()]
param(
    [string] $HostName = 'com.autolang.bridge',
    [string] $ExtensionId = 'iblcjhakhfggopgijnankilmifbjbdbp',
    [int] $TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\AutoLang'
$manifestPath = Join-Path $installDir "$HostName.json"
$exePath = Join-Path $installDir 'AutoLang.exe'

# Written by the out-of-process reader and read back by this one. If the filesystem were redirected
# too, this file would never appear from here and the script reports that it could not verify -
# which is the correct answer, rather than a false pass.
$reportPath = Join-Path $installDir 'registration-check.txt'

$browsers = [ordered]@{
    Chrome = "HKCU\Software\Google\Chrome\NativeMessagingHosts\$HostName"
    Edge   = "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
}

Write-Host "Verifying the native messaging registration for $HostName" -ForegroundColor Cyan
Write-Host "  as a separate process sees it, not as this one does`n"

# --- Ask another process -----------------------------------------------------------------------

if (Test-Path $reportPath) { Remove-Item $reportPath -Force }

$sections = foreach ($name in $browsers.Keys) {
    "echo ---$name--- & reg query `"$($browsers[$name])`" /ve"
}
$sections += "echo ---MANIFEST--- & type `"$manifestPath`""
$sections += "echo ---EXE--- & if exist `"$exePath`" (echo present) else (echo MISSING)"

$command = "cmd.exe /c ($($sections -join ' & ')) > `"$reportPath`" 2>&1"

try {
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $command }
} catch {
    Write-Host "could not start an out-of-process reader through WMI: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host 'The registration was NOT verified. This is not a failure of the registration itself.'
    exit 2
}

if ($result.ReturnValue -ne 0) {
    Write-Host "WMI refused to start the reader (code $($result.ReturnValue))." -ForegroundColor Yellow
    Write-Host 'The registration was NOT verified.'
    exit 2
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not (Test-Path $reportPath)) { Start-Sleep -Milliseconds 200 }

if (-not (Test-Path $reportPath)) {
    Write-Host 'The out-of-process reader produced nothing this process can read.' -ForegroundColor Yellow
    Write-Host 'That usually means the filesystem is redirected as well. The registration was NOT verified.'
    exit 2
}

# The reader writes and exits; give the redirection a moment to settle before reading.
Start-Sleep -Milliseconds 400
$report = Get-Content $reportPath -Raw
Remove-Item $reportPath -Force -ErrorAction SilentlyContinue

# --- Judge it ----------------------------------------------------------------------------------

$failures = @()

foreach ($name in $browsers.Keys) {
    $section = ($report -split "---$name---")[1]
    if ($null -eq $section) { $failures += "$name`: the reader produced no output for this key"; continue }
    $section = ($section -split '---')[0]

    if ($section -match 'unable to find') {
        $failures += "$name`: $($browsers[$name]) does not exist outside this process"
        Write-Host ("  {0,-8} MISSING" -f $name) -ForegroundColor Red
    } elseif ($section -notmatch [regex]::Escape($manifestPath)) {
        $failures += "$name`: the key exists but does not point at $manifestPath"
        Write-Host ("  {0,-8} WRONG PATH" -f $name) -ForegroundColor Red
    } else {
        Write-Host ("  {0,-8} ok" -f $name) -ForegroundColor Green
    }
}

$manifestSection = ($report -split '---MANIFEST---')[1]
if ($manifestSection) { $manifestSection = ($manifestSection -split '---EXE---')[0] }

if (-not $manifestSection -or $manifestSection -match 'cannot find|The system cannot') {
    $failures += "the manifest at $manifestPath is not readable outside this process"
    Write-Host '  manifest MISSING' -ForegroundColor Red
} elseif ($manifestSection -notmatch [regex]::Escape($ExtensionId)) {
    $failures += "the manifest does not allow extension $ExtensionId"
    Write-Host '  manifest WRONG EXTENSION ID' -ForegroundColor Red
} else {
    Write-Host '  manifest ok' -ForegroundColor Green
}

if ($report -match '---EXE---\s*MISSING') {
    $failures += "$exePath does not exist outside this process"
    Write-Host '  agent    MISSING' -ForegroundColor Red
} else {
    Write-Host '  agent    ok' -ForegroundColor Green
}

# --- Say what it means -------------------------------------------------------------------------

if ($failures.Count -eq 0) {
    Write-Host "`nThe registration is visible to other processes. A browser will find this host." -ForegroundColor Green
    exit 0
}

Write-Host "`nThe registration is NOT visible to other processes:" -ForegroundColor Red
foreach ($failure in $failures) { Write-Host "  - $failure" }

Write-Host @"

The browser will report "Specified native messaging host not found" and nothing checked from inside
the installing shell will explain why.

If the install was run by an automated tool, an agent, or any sandboxed shell, that is the cause:
its registry writes went to a private view. Run the installer again from an ordinary PowerShell
window - one you opened yourself from the Start menu - and run this check again.
"@ -ForegroundColor Yellow

exit 1
