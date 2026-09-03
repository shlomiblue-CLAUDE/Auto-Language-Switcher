<#
.SYNOPSIS
    Reads the Agent's verbose log and looks for the two failures that a passing test suite missed.

.DESCRIPTION
    Both defects found during acceptance were about what the product learns over time, not what it
    decides once, so no unit test could see them and no single log line looked wrong. They were
    found by noticing a pattern across many lines. This finds those patterns mechanically.

    What it looks for:

      Flip-flop      One conversation switched to a different language within a few seconds.
                     Never legitimate. Two switches close together are fine when they are two
                     different conversations, which is why this compares keys and not just times.

      Memory churn   One conversation whose remembered language changed more than once in a
                     session. That was the signature of the typing guard learning from the
                     product's own output: memory went Hebrew, English, Hebrew in thirty seconds.

      Failed switch  Windows refused the layout change. Rare and worth knowing about.

      Stale signals  Observations discarded for arriving too late to act on.

    Everything else is reported as counts, because a healthy session is mostly AlreadyCorrect and
    that is exactly what it should look like.

    Turn logging on first - it is off unless the Agent is started with --verbose:

        Get-Process AutoLang | Stop-Process -Force
        & "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --verbose

.PARAMETER Path
    Log file. Defaults to the installed location.

.PARAMETER FlipFlopWindowSeconds
    How close two opposing switches on one conversation must be to count as a flip-flop.

.PARAMETER Isolated
    Read the log through a process started outside this one.

    For sandboxed or automated shells only. They can be handed a private, stale copy of the file,
    which during acceptance made a store holding six conversations look like it held one. A person
    at an ordinary prompt does not need this.

.EXAMPLE
    .\review-log.ps1
    .\review-log.ps1 -Isolated
#>

[CmdletBinding()]
param(
    [string] $Path = (Join-Path $env:LOCALAPPDATA 'AutoLang\agent.log'),
    [int] $FlipFlopWindowSeconds = 5,
    [switch] $Isolated
)

$ErrorActionPreference = 'Stop'

# --- Get the text ------------------------------------------------------------------------------

if ($Isolated) {
    $copy = Join-Path ([System.IO.Path]::GetTempPath()) 'autolang-log-review.txt'
    if (Test-Path $copy) { Remove-Item $copy -Force }

    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "cmd.exe /c type `"$Path`" > `"$copy`" 2>&1"
    }
    if ($result.ReturnValue -ne 0) { throw "Could not start an out-of-process reader (code $($result.ReturnValue))." }

    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $copy)) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $copy)) { throw 'The out-of-process reader produced nothing this process can read.' }

    Start-Sleep -Milliseconds 300
    $lines = Get-Content $copy
    Remove-Item $copy -Force -ErrorAction SilentlyContinue
} else {
    if (-not (Test-Path $Path)) {
        Write-Host "No log at $Path." -ForegroundColor Yellow
        Write-Host 'The Agent only writes one when started with --verbose. See the help in this file.'
        exit 2
    }
    $lines = Get-Content $Path
}

Write-Host "Reviewing $Path" -ForegroundColor Cyan
Write-Host "  $($lines.Count) lines`n"

# --- Parse -------------------------------------------------------------------------------------

$decisions = foreach ($line in $lines) {
    if ($line -match '^\[(?<at>[\d\- :\.]+)\] decision (?<key>[0-9a-f]{8}): (?<outcome>\w+)') {
        [pscustomobject]@{
            At       = [datetime]::Parse($Matches.at)
            Key      = $Matches.key
            Outcome  = $Matches.outcome
            Blocker  = if ($line -match 'blocker=(\w+)') { $Matches[1] } else { $null }
            Language = if ($line -match ' lang=(\w+)') { $Matches[1] } else { $null }
            Source   = if ($line -match ' src=(\w+)') { $Matches[1] } else { $null }
            Memory   = if ($line -match ' memory=(\w+)') { $Matches[1] } else { $null }
            Line     = $line
        }
    }
}

$switches = $decisions | Where-Object { $_.Outcome -eq 'Switch' }

# --- Counts ------------------------------------------------------------------------------------

Write-Host 'Activity'
Write-Host ("  {0,-22} {1}" -f 'decisions', $decisions.Count)
Write-Host ("  {0,-22} {1}" -f 'switches applied', $switches.Count)
Write-Host ("  {0,-22} {1}" -f 'conversations', ($decisions.Key | Sort-Object -Unique).Count)

$latencies = foreach ($line in $lines) { if ($line -match 'switched to \w+ in (\d+)ms') { [int]$Matches[1] } }
if ($latencies) {
    $stats = $latencies | Measure-Object -Average -Maximum
    Write-Host ("  {0,-22} {1:F0}ms average, {2}ms worst" -f 'switch latency', $stats.Average, $stats.Maximum)
}

if ($switches) {
    Write-Host "`nEvidence used"
    $switches | Group-Object Source | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0,-22} {1}" -f $_.Name, $_.Count)
    }
}

$blocked = $decisions | Where-Object { $_.Blocker }
if ($blocked) {
    Write-Host "`nWhy nothing happened"
    $blocked | Group-Object Blocker | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0,-22} {1}" -f $_.Name, $_.Count)
    }
    Write-Host '  (AlreadyCorrect dominating is what a healthy session looks like)' -ForegroundColor DarkGray
}

# --- Suspicions ------------------------------------------------------------------------------

$problems = @()

# Flip-flop, per conversation. Comparing within a key is the whole point: two switches a second
# apart across two different conversations is correct behaviour and must not be flagged.
foreach ($group in $switches | Group-Object Key) {
    $ordered = $group.Group | Sort-Object At
    for ($i = 1; $i -lt $ordered.Count; $i++) {
        $gap = ($ordered[$i].At - $ordered[$i - 1].At).TotalSeconds
        if ($gap -le $FlipFlopWindowSeconds -and $ordered[$i].Language -ne $ordered[$i - 1].Language) {
            $problems += "flip-flop on $($group.Name): $($ordered[$i-1].Language) then $($ordered[$i].Language) " +
                         "after {0:F1}s at $($ordered[$i].At.ToString('HH:mm:ss'))" -f $gap
        }
    }
}

# Memory churn. One change is a conversation whose language genuinely moved. Repeated changes are
# the product arguing with itself, which is what the typing guard used to do.
foreach ($group in $decisions | Where-Object { $_.Memory -and $_.Memory -ne 'none' } | Group-Object Key) {
    $seen = $group.Group | Sort-Object At | ForEach-Object { $_.Memory }
    $changes = 0
    for ($i = 1; $i -lt $seen.Count; $i++) { if ($seen[$i] -ne $seen[$i - 1]) { $changes++ } }
    if ($changes -gt 1) {
        $problems += "memory churn on $($group.Name): remembered language changed $changes times " +
                     "($((($seen | Get-Unique) -join ' -> ')))"
    }
}

foreach ($line in $lines) {
    if ($line -match 'switch to (\w+) failed: (\S+)') { $problems += "failed switch to $($Matches[1]): $($Matches[2])" }
    if ($line -match 'ignoring stale signal, age (\d+)ms') { $problems += "stale signal discarded, age $($Matches[1])ms" }
}

# --- Verdict -----------------------------------------------------------------------------------

Write-Host ''
if ($problems.Count -eq 0) {
    Write-Host 'Nothing suspicious. No flip-flop, no memory churn, no failed switch.' -ForegroundColor Green
    exit 0
}

Write-Host "$($problems.Count) thing(s) worth looking at:" -ForegroundColor Yellow
foreach ($problem in $problems | Select-Object -Unique) { Write-Host "  - $problem" }
Write-Host "`nThese are the shapes of the two defects found during acceptance. See docs/ACCEPTANCE.md."
exit 1
