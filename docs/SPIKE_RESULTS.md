# Phase 0 — Technical Spike Results

Date: 2026-09-02
Machine: Windows 11 Education 10.0.26100, x64
Gate status: **PASSED** — layout switching is viable. Product build may proceed.

## Why this spike existed

The PDR (§9, §18) names one risk that could invalidate the entire product: Windows may tie
keyboard layout to a thread or window in a way that a helper process cannot reliably influence.
Section 20 required proving this before writing product code, and warned specifically:
*"אין להכריז על הצלחה רק משום ש־API החזיר ערך"*. Every result below is verified by re-reading
`GetKeyboardLayout(threadId)` after the attempt, not by trusting a Win32 return value.

## Toolchain

| Tool | Result |
|---|---|
| Node.js | Already installed, v24.19.0 at `C:\Program Files\nodejs` (was simply absent from the shell PATH, not missing) |
| npm | 11.17.0 |
| .NET 8 SDK | winget install **failed, exit 1602** — the MSI requires elevation and UAC was declined. Installed per-user instead via the official `dot.net/v1/dotnet-install.ps1` to `%LOCALAPPDATA%\Microsoft\dotnet`. SDK **8.0.424** |
| Consequence | A per-user .NET requires `DOTNET_ROOT` to be set or apphost-built exes fail with `hostfxr.dll not found` (exit 131). Set persistently in the user environment. **This does not affect shipping**, since the plan already publishes self-contained + trimmed. |

## Finding 1 — the KLID in the PDR would not have worked

PDR §9 proposes `0000040D` for Hebrew. What Windows actually reports here:

```
0xF03D040D  langid=0x040D  klid=0002040d  Hebrew (Standard)
0x04090409  langid=0x0409  klid=00000409  US
```

`HKCU\Keyboard Layout\Substitutes` maps `0000040d → 0002040d`. Hardcoding `0000040D` would have
matched nothing on this machine.

**Implemented rule:** resolve by **LANGID** (low word of the HKL) against `GetKeyboardLayoutList`,
never by a hardcoded KLID string. `KlidFromHkl` additionally decodes the `0xF000`-marked high word
through `HKLM\SYSTEM\...\Keyboard Layouts\*\Layout Id` for diagnostics only.

Note the HKL is sign-extended to 64-bit (`0xFFFFFFFFF03D040D`). Comparisons use the full `IntPtr`
as returned by Windows; only display is masked to 32 bits.

## Finding 2 — strategy matrix

Each run cycles `en → he → en` so a strategy that only switches one direction is caught.
`-noop-` rows are excluded from scoring (the layout was already the target).

### Chrome (genuinely foreground)

```
strategy           lang  result before      after       ms
post-toplevel      en    PASS   0xF03D040D  0x04090409  22
post-toplevel      he    PASS   0x04090409  0xF03D040D  27
post-toplevel      en    PASS   0xF03D040D  0x04090409  25
post-focus         he    PASS   0x04090409  0xF03D040D  27
post-focus         en    PASS   0xF03D040D  0x04090409  27
post-syscharset    he    PASS   0x04090409  0xF03D040D  27
post-syscharset    en    PASS   0xF03D040D  0x04090409  27
attach-activate    he    FAIL   0x04090409  0x04090409  401 (timeout)
```

### Edge (foreground — closed 2026-09-03)

```
strategy           lang  result before       after        ms
post-toplevel      en    PASS   0xF03D040D   0x04090409   25
post-toplevel      he    PASS   0x04090409   0xF03D040D   16
post-toplevel      en    PASS   0xF03D040D   0x04090409   17
post-focus         he    PASS   0x04090409   0xF03D040D   16
post-focus         en    PASS   0xF03D040D   0x04090409   15
post-syscharset    he    PASS   0x04090409   0xF03D040D   16
post-syscharset    en    PASS   0xF03D040D   0x04090409   15
attach-activate    he    FAIL   0x04090409   0x04090409   403 (timeout)
7/8 real transitions passed against msedge (foreground=True)
```

Identical to Chrome, including which strategy fails. 15–25ms.

Getting Edge to the foreground took two attempts, and the failure is worth recording because it is
the same lock the product relies on. `SetForegroundWindow` was refused outright; adding
`AttachThreadInput` was still refused. What worked was a synthetic ALT press first — Windows grants
foreground rights to a process it believes the user just interacted with. The spike reports whether
each attachment succeeded rather than assuming.

**The product must never do this.** A keyboard switcher that grabs focus would be worse than the
problem it solves. The trick lives in the spike, which is diagnostic code.

The whole product was then run against that foreground Edge window — the installed 12.6MB binary,
through native messaging framing, over the named pipe, through the decision engine:

```json
{"outcome":"Switch","language":"he-IL","confidence":1,"source":"OutgoingMessages","applied":true}
```

### Edge (background window — the original run)

```
post-toplevel      en    PASS   0xF03D040D  0x04090409  23
post-toplevel      he    PASS   0x04090409  0xF03D040D  24
post-toplevel      en    PASS   0xF03D040D  0x04090409  26
post-focus         he    PASS   0x04090409  0xF03D040D  27
post-focus         en    PASS   0xF03D040D  0x04090409  27
post-syscharset    he    PASS   0x04090409  0xF03D040D  28
post-syscharset    en    PASS   0xF03D040D  0x04090409  28
attach-activate    he    FAIL   0x04090409  0x04090409  413 (timeout)
7/8 real transitions passed
```

**Chosen strategy: `post-toplevel`** — `PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)`.
Simplest, no thread attachment, works on both browsers.

Correction to the plan: `wParam` must be **0**, not `INPUTLANGCHANGE_FORWARD`. `FORWARD`/`BACKWARD`
mean "cycle to next/previous layout" and cause Windows to **ignore the explicit HKL in lParam** —
exactly the blind cycling PDR §9 forbids.

`attach-activate` (`AttachThreadInput` + `ActivateKeyboardLayout`) fails to reach Hebrew on both
browsers and is **rejected**. It is not needed as a fallback.

## Finding 3 — latency

**22–28ms** for every successful transition, measured by polling until `GetKeyboardLayout` reports
the target. The PDR budget is 100–500ms; the switch itself uses roughly 5% of it. The remaining
budget belongs to DOM observation, debounce and IPC.

## Finding 4 — background windows are NOT protected by Windows

The Edge matrix ran against a window that was **not** foreground (`foreground=NO`) and every
transition still succeeded.

This is the most operationally important result. Windows will happily change the layout of a
background window, so PDR §12 "Focus guard" and acceptance test *"אירוע מלשונית ברקע אינו משנה את
השפה בחלון אחר"* are **entirely the product's responsibility**. The Agent must check
`GetForegroundWindow()` and reject the request itself; there is no OS safety net.

## Finding 5 — per-window input mode is active here

Chrome and Edge held **different layouts simultaneously** during the runs (Edge sat at Hebrew while
Chrome had been left at English). That is direct evidence that "Let me use a different input method
for each app window" is **ON** on this machine, which is the mode the product is designed around.

## Open items — not yet proven

Recorded honestly rather than assumed:

1. ~~**Edge as a true foreground window.**~~ **Closed 2026-09-03** — see the foreground matrix
   above. 7/8, identical to Chrome, plus a real switch applied by the shipped product.

2. **Per-window input mode OFF.** Still untested. With a system-wide layout, switching would affect
   every window at once, making the foreground guard critical rather than merely correct. Requires
   toggling a system setting, so it is left for the user to decide.

3. ~~**Native messaging round trip.**~~ **Closed in phase 4** — `tools/bridge-smoke-test.mjs`
   drives the real chain across three processes on every run.

## Commands

```bash
AutoLangSpike list                          # enumerate layouts and show resolution
AutoLangSpike watch 500                     # live foreground window + layout monitor
AutoLangSpike set he --delay 5              # single switch against the foreground window
AutoLangSpike matrix --delay 6              # full strategy matrix, foreground
AutoLangSpike matrix --process msedge       # target a named process instead of foreground
```
