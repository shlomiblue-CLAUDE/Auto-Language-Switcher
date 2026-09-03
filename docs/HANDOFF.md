# Handoff

Context for a new session. Read this first; it is shorter than the code and it contains things the
code cannot tell you — chiefly one environment trap that cost a full day and will cost it again.

---

## What this is

**Auto Language Switcher.** A Chrome extension plus a resident Windows agent that switches the
Windows keyboard layout to the language you are about to type in, per conversation. Hebrew and
English in v1. Built from `PDR_Auto_Language_Switcher_Claude_Code_HE (1).docx`.

Entirely local: no API, no server, no telemetry, no remote code. That is the product's central
claim and a release-gating audit enforces it (`tools/privacy-audit.mjs`, wired into the build).

**Repository:** `D:\claude workspace\H-E`, branch `master`.

**Status: working end to end on the user's machine.** All four manual acceptance rows pass. Two
rows remain deliberately unmarked — see [Open](#open).

---

## Read this before touching the installer

**Claude Code's shell has a virtualised registry.** Writes to `HKCU` go into a private view: the
writing process reads its own value straight back and sees it there, while every other process on
the machine — the browser included — finds nothing at that path.

This is not detectable from inside. `reg query` prints the key. `Get-ItemProperty` returns the
value. `HKEY_USERS\<SID>` returns the same virtualised view. `GetCurrentPackageFullName` reports no
package identity, so it is not MSIX. The only question that separates the two cases is whether a
*different* process can see the value.

It presented as the extension reporting `Specified native messaging host not found` while every
check passed: registry key present with the right default value, manifest valid and BOM-free
pointing at a file that existed, host name matching in three places, no enterprise policy, fourteen
browser variants registered. Chrome was right and every check was wrong.

**So: never run `Install.ps1` directly from this shell.** Run it out of process:

```powershell
# write a .cmd first, then:
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'cmd.exe /c C:\path\to\wrapper.cmd' }
```

The wrapper should call `Install.ps1 -Verify`. `-Verify` runs `tools/verify-registration.ps1`,
which reads the registration back through a separate process and fails loudly if it is invisible.
It is proven to catch the failure: deleting the real key and installing from inside the sandbox
makes it report `Chrome MISSING / Edge MISSING` and throw.

The same applies to **reading** `%LOCALAPPDATA%\AutoLang\`. A direct read here can return a stale
private copy — that produced a false alarm during acceptance, where the store appeared to hold one
fixture conversation while it really held six real ones. Read it through WMI, or use
`tools/review-log.ps1 -Isolated`.

Files are not redirected. Only the registry, and store files that this shell has written.

---

## Architecture

```
Content script (WhatsApp Web)          [phase 2: desktop source, not built]
        │ letter counts only                    │
        ▼                                       │
  Service worker ──native messaging──►  AutoLang.exe (bridge mode)
        ▲                                       │ named pipe
        └────────── state/decision ─────────────┤
                                                ▼
                              AutoLang.exe (agent, tray, resident)
                              ├── LanguageDetector    (pure)
                              ├── DecisionEngine      (pure)
                              ├── ConversationStore   (%LOCALAPPDATA%\AutoLang)
                              └── KeyboardLayoutService (Win32)
```

**Two deliberate departures from the PDR**, both load-bearing:

1. **The decision engine lives in the Agent, not the service worker.** It is what makes a future
   desktop signal source possible without a second implementation of the rules in another language,
   and it removes the MV3 service-worker eviction risk entirely.
2. **The native host is not a separate executable.** One `AutoLang.exe` runs as agent or as bridge
   depending on argv. Chrome spawns a fresh bridge per connection; it forwards over a named pipe to
   the resident agent. Merging them cut the published size from 211MB to 12.6MB.

**Layout switching:** `PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)`. wParam **must be 0** —
`FORWARD`/`BACKWARD` mean "cycle" and make Windows ignore the explicit HKL. Layouts are resolved by
LANGID against `GetKeyboardLayoutList` at runtime, never by hardcoded KLID: this machine has Hebrew
as `0002040D`, not the `0000040D` the PDR assumed.

**Privacy by construction:** the content script counts letters and discards the text inside one
function. `MessageStats` is what crosses every boundary — there is no field anywhere in the wire
contract that could carry message content. Conversation identity is a salted SHA-256 of the chat
title, hashed in the page; the Agent **refuses** any key that is not 32 lowercase hex characters.

---

## Working on it

```powershell
# everything: tests, icons, extension bundle, agent publish, privacy audit, store preflight, zip
.\build.ps1

# install (from an ordinary shell, or out of process — see above)
.\installer\Install.ps1 -Verify

# is the product behaving?
.\tools\review-log.ps1            # add -Isolated from a sandboxed shell
```

Node and dotnet are not on this shell's PATH. Prepend:

```powershell
$env:Path = "C:\Program Files\nodejs;$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
```

There is no `agent.sln`; test the two projects individually.

**273 tests: 124 Core, 85 Agent, 64 TypeScript.** The build fails on any of them, on a privacy audit
failure, or on a store preflight failure.

Verbose logging is off by default and is the only way to see decisions:

```powershell
Get-Process AutoLang | Stop-Process -Force
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --verbose
```

Autostart does **not** pass `--verbose`, so it is quiet after the next sign-in. It is currently on.

---

## Defects found the hard way

Do not reintroduce these. Each has tests; the comments explain the reasoning at each site.

**The adapter matched every selector and understood nothing.** WhatsApp changed its DOM: the
`message-in`/`message-out` classes are gone and `data-id` no longer carries a JID. 49 green tests
said nothing because the fixtures described the 2024 DOM. Direction is now resolved by **geometry** —
bubble distance to panel edges, against `getComputedStyle(panel).direction`, because RTL mirrors and
in a Hebrew UI the user's own messages are on the **left**. `checkHealth` now reports whether
direction could actually be read, not merely whether selectors matched.

**The weak fallback was written to memory.** Incoming messages are the other person's language — a
hint worth acting on once, never worth recording, because memory outranks the user's own messages on
every later visit. A conversation where the other person writes Hebrew and the user answers in
English learned Hebrew once and answered Hebrew forever.

**The typing guard learned from the product's own output.** It records the layout in use while the
user types, which is sound, but it did not ask where that layout came from. After an automatic
switch the layout in use is the last guess, so the moment the user began typing that guess became
the conversation's remembered language. One wrong switch became permanent. The guard now declines to
learn a layout the engine imposed and the user has not touched since; a manual correction hands it
back and learning resumes.

Together these two produced the user's report: *"it started well, then got worse, and clearing the
memory fixes it."*

**Suppressed decisions dropped their source and confidence,** so a conversation read in full
reported source `None` at confidence `0.00` — indistinguishable from having seen nothing. That is
what the popup shows the user as the reason, and it misled a round of diagnosis.

**The log recorded only applied switches,** which made "it did not switch" undiagnosable: suppressed,
already-correct, and no-decision all looked like silence. A session with four conversation changes
produced no lines at all. It now logs every decision with the evidence behind it, split by who wrote
the messages — which is what separated "genuinely mixed conversation" from "direction detection
failed" in a single reading.

**Smaller ones, all with tests or guards:** a BOM in the host manifest (PS 5.1 `Out-File -Encoding
utf8` writes one; Chrome then reports the host as missing); registering only Chrome and Edge rather
than all fourteen Chromium variants; the content script looping forever against an invalidated
extension context; `checkHealth` calling the sign-in screen a WhatsApp redesign; a store zip older
than the build.

**A caution about my own conclusions:** I twice over-concluded from one data point — declared
conversation identity broken when fourteen keys were fourteen different chats, and declared
conversation memory dead when I had read a sandbox copy of the store. Both are corrected in
`docs/ACCEPTANCE.md`. Check which process's view you are reading before believing a file.

---

## Map

| | |
|---|---|
| `agent/AutoLang.Core/` | Pure, testable, no Windows dependency. Detector, decision engine, store, wire types |
| `agent/AutoLang.Agent/` | Win32. Layout service, pipe server, bridge mode, tray, `AgentCore` |
| `extension/src/adapters/whatsapp/` | Selectors, geometry-based direction, adapter. The fragile part |
| `extension/src/content/` | Observer: debounce, mutation, focus, visibility |
| `tools/verify-registration.ps1` | Is the registration visible to other processes? |
| `tools/review-log.ps1` | Flip-flop and memory-churn detector. Proven against a fixture |
| `tools/privacy-audit.mjs` | Release gate. Proven to catch a planted phone number |
| `tools/store-preflight.mjs` | Derives the extension ID from the manifest key |
| `docs/ACCEPTANCE.md` | The sixteen rows, what passed, and what two rounds of C found |
| `docs/STORE_LISTING.md` | Listing copy, permission justifications, launch order |

**Extension ID `iblcjhakhfggopgijnankilmifbjbdbp`**, fixed by the `key` field in the manifest. The
native host allowlist names it exactly. Remove `key` and every install fails while the extension
looks perfectly healthy — `store-preflight.mjs` recomputes the ID from the key to catch that.

---

## Open

**Two acceptance rows, deliberately unmarked.** CPU and memory under sustained load, and the
alt-tab check that another window's layout is left alone. Not observed. An assumed pass is worth
less than an empty row.

**Untested by choice:** the Windows setting "let me use a different input method for each app
window" in its OFF state. It changes behaviour materially and the user was left to decide.

**Launch, none of it code.** In the order the lead times demand:

1. **Code-signing certificate.** Not started, and the longest pole by far. Without it SmartScreen
   blocks the installer and almost nobody installs. OV needs weeks of reputation; EV is immediate
   and costs more.
2. **Privacy policy at a public URL.** Mandatory for Chrome Web Store approval. `web/privacy.html`
   is written and needs hosting.
3. **Three 1280×800 screenshots** from a *test* account with invented conversations. Not the real
   account — a privacy claim its own screenshots contradict is worse than none.
4. **Submit** to Chrome Web Store, then Edge Add-ons (same package, shorter queue).

---

## The user

Hebrew-speaking; reply in Hebrew. Runs Chrome 152 on Windows 11. Reports symptoms precisely and
accurately — *"it started well then got worse, clearing memory fixes it"* described a circular
learning defect exactly, and was right when my first two hypotheses were wrong. Take the reports
literally and go to the log.
