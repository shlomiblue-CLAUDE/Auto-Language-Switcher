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

.PARAMETER Since
    Ignore everything logged before this moment.

    The log is cumulative and the Agent never truncates it, so a review a week into real use is
    mostly a review of the week before - including defects that were found and fixed in between.
    Reporting those again as though they were live is how a fix stops being believed.

    Pass the moment the version under test started running. `-Since '2026-09-05 01:00'` works, and
    so does `-Since (Get-Date).AddDays(-1)`.

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
    [datetime] $Since,
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

if ($PSBoundParameters.ContainsKey('Since')) {
    $before = $lines.Count

    # Every line the Agent writes starts with its own timestamp, so this is a filter and not a
    # guess. A line that does not parse is dropped rather than kept: keeping it would quietly
    # readmit the history this switch exists to exclude.
    $lines = $lines | Where-Object {
        $_ -match '^\[(?<at>[\d\- :\.]+)\]' -and [datetime]::Parse($Matches.at) -ge $Since
    }

    Write-Host ("  {0} lines, from {1} since {2:yyyy-MM-dd HH:mm}" -f $lines.Count, $before, $Since)

    if ($lines.Count -eq 0) {
        Write-Host "`nNothing logged after that moment." -ForegroundColor Yellow
        Write-Host 'Either nothing has happened yet, or the Agent is not running with --verbose.'
        exit 2
    }
    Write-Host ''
} else {
    Write-Host "  $($lines.Count) lines`n"
}

# --- Parse -------------------------------------------------------------------------------------

# The " on <where>" segment is optional because it was added to the log after this tool was
# written, and the tool did not notice. It went on reporting "Nothing suspicious" over lines it
# could no longer read: 2120 of 2567 decisions on this machine, everything after the day the site
# name was added. A parser that silently matches nothing is worse than one that crashes, so the
# count of unreadable decision lines is now printed rather than assumed to be zero.
$decisions = foreach ($line in $lines) {
    if ($line -match '^\[(?<at>[\d\- :\.]+)\] decision (?<key>[0-9a-f]{8})(?: on (?<where>[^:]+))?: (?<outcome>\w+)') {
        [pscustomobject]@{
            At       = [datetime]::Parse($Matches.at)
            Key      = $Matches.key
            Where    = if ($Matches.where) { $Matches.where } else { 'outside the browser' }
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

# The check that would have caught the silence above, and the reason it is loud rather than a
# footnote: every unread decision is a defect this tool cannot see, reported to you as a clean bill
# of health.
$decisionLines = @($lines | Where-Object { $_ -match ' decision ' }).Count
$unread = $decisionLines - @($decisions).Count
if ($unread -gt 0) {
    Write-Host ("WARNING: {0} of {1} decision lines did not parse. This tool is not reading the log." -f $unread, $decisionLines) -ForegroundColor Red
    Write-Host "         The log format has moved on. Fix the pattern in this file before trusting anything below.`n"
}

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
            # The date belongs here. A bare time reads as "this happened today", and a log that
            # spans a week of real use makes that wrong most of the time.
            $problems += "flip-flop on $($group.Name) ($($ordered[$i].Where)): " +
                         "$($ordered[$i-1].Language) then $($ordered[$i].Language) " +
                         "after {0:F1}s at $($ordered[$i].At.ToString('yyyy-MM-dd HH:mm:ss'))" -f $gap
        }
    }
}

# Flip-flop between two conversations in one place, which the per-key loop above cannot see.
#
# Two windows of one application, or two boxes on one page, each remembering a different language,
# can take turns undoing each other's layout while the user sits still. Every individual key looks
# perfectly consistent - it switches to the language it remembers, every time - so grouping by key
# reports nothing at all. It was found by reading a five-minute stretch of log by hand, which is
# not a repeatable way to find anything.
#
# What counts is the same PAIR going back and forth - A, B, A - and not a single A to B.
#
# The first version of this check flagged every adjacent pair of switches in one place, and its
# own output killed it: it reported thirty-seven findings on WhatsApp Web, every one of them the
# user moving from an English chat to a Hebrew one two seconds later. That is not a defect, it is
# the product. Three different keys in a row is somebody scrolling through their conversations.
#
# A fight looks different: one pair of keys, reversing each other repeatedly.
#
# It is counted per pair over ten minutes rather than between adjacent switches, because the second
# attempt at this check missed the very case it was written for. The two Claude windows reversed
# each other four times in eight minutes, but roughly ninety seconds apart, so anything that
# required the alternations to be adjacent in time discarded them. Rate is the signal, not
# proximity: chat-hopping produces many pairs once each, and a fight produces one pair many times.
#
# It cannot go further than "read this", and the reason is worth being honest about: somebody
# answering a Hebrew friend and an English colleague in turn produces exactly this shape, and that
# is the product's main use case rather than a defect. What separates the two is not in these
# lines - in the run that prompted this check, every switch by one of the two Claude keys came back
# SWITCH_FAILED, meaning the window it named was not the one in front. So treat a hit here as a
# stretch of log to read, never as a count of things that are wrong.
$PairWindowMinutes = 10
foreach ($place in $switches | Group-Object Where) {
    $inPlace = @($place.Group | Sort-Object At)

    $reversals = @(for ($i = 1; $i -lt $inPlace.Count; $i++) {
        if ($inPlace[$i].Key -ne $inPlace[$i - 1].Key -and $inPlace[$i].Language -ne $inPlace[$i - 1].Language) {
            [pscustomobject]@{
                At   = $inPlace[$i].At
                Pair = (@($inPlace[$i - 1].Key, $inPlace[$i].Key) | Sort-Object) -join ' and '
            }
        }
    })

    foreach ($pair in $reversals | Group-Object Pair) {
        $times = @($pair.Group.At | Sort-Object)
        for ($i = 0; $i -lt $times.Count; $i++) {
            $burst = @($times | Where-Object { $_ -ge $times[$i] -and ($_ - $times[$i]).TotalMinutes -le $PairWindowMinutes })
            if ($burst.Count -ge 3) {
                $problems += "two contexts on $($place.Name) reversing each other: $($pair.Name), " +
                             "$($burst.Count) times from $($burst[0].ToString('yyyy-MM-dd HH:mm:ss')) " +
                             "to $($burst[-1].ToString('HH:mm:ss'))"
                break
            }
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

# Counted after the dedupe, not before. It used to announce eleven things and then print seven,
# which sends you looking for four findings that were never there.
$unique = @($problems | Select-Object -Unique)
Write-Host "$($unique.Count) thing(s) worth looking at:" -ForegroundColor Yellow
foreach ($problem in $unique) { Write-Host "  - $problem" }
Write-Host "`nThese are shapes worth reading, not a count of defects - two contexts reversing each"
Write-Host "other is also what a person answering two people in turn looks like. Go to the lines."
exit 1
