# Handoff

Context for a new session. Read this first; it is shorter than the code and it contains things the
code cannot tell you — chiefly one environment trap that cost a full day and will cost it again.

---

## What this is

**Auto Language Switcher.** A Chrome extension plus a resident Windows agent that switches the
Windows keyboard layout to the language you are about to type in. Hebrew and English in v1. Built
from `PDR_Auto_Language_Switcher_Claude_Code_HE (1).docx`, which describes WhatsApp Web only.

It now works **anywhere the user writes**, in the browser and outside it.

- **In the browser:** WhatsApp Web has a hand-written adapter and is granted at install; every other
  site is opt-in from the popup, one origin at a time or all of them at once. Memory is per writing
  box, so a Hebrew message and an English search on the same page stay separate.
- **Outside it:** applications the user adds in settings — Slack, an editor, anything with a
  window. Memory is per window. Nothing inside them is read, and that is not a shortcut; see
  [Outside the browser](#outside-the-browser).
- **Five languages:** Hebrew, English, Russian, Arabic, Greek — the ones whose *script* the
  detector can tell apart. Which of them the product may use is a setting.

Entirely local: no API, no server, no telemetry, no remote code. That is the product's central
claim and a release-gating audit enforces it (`tools/privacy-audit.mjs`, wired into the build).

**Repository:** `D:\claude workspace\H-E`, branch `main` — the only branch. Backed up to a private
GitHub repo, `shlomiblue-CLAUDE/Auto-Language-Switcher`.

**`secrets/` is not in it, and must never be.** It is gitignored, and the private key belongs
nowhere near a remote.

What that costs is less than it sounds, and I first wrote the opposite here. The extension **ID
survives without the private key**, because it is derived from the *public* key — the `key` field
in `extension/public/manifest.json`, which is committed and backed up, and which
`store-preflight.mjs` recomputes the ID from. Nothing in the build or the installer reads the
`.pem`; only `tools/bridge-smoke-test.mjs` touches `secrets/`, and only for `extension-id.txt`,
which it falls back without.

The `.pem` is needed to sign a `.crx` for distribution outside the store, and to regenerate the
`key` field. Neither is on the launch path, since the Chrome Web Store signs. Worth keeping a copy
somewhere; not the single point of failure this paragraph used to claim it was.

**Status: working end to end on the user's machine**, on WhatsApp Web and on ordinary sites. Every
manual acceptance row has now been run. Two results are worth more than the ticks: the eleven
generic-site rows passed while the product was still overriding the keyboard on Google Sheets, and
E13 fails there and passes on Gmail. See [Open](#open).

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
fixture conversation while it really held six. Read it through WMI, or use
`tools/review-log.ps1 -Isolated`.

Files are not redirected. Only the registry, and store files that this shell has written.

---

## Architecture

```
Content script                              ForegroundWatcher (in the Agent)
  WhatsAppAdapter → GenericAdapter          SetWinEventHook, allowlisted
        │ letter counts only                        │ process + hashed window title
        ▼                                           │ no text, ever
  Service worker ──native messaging──►  AutoLang.exe (bridge mode)
   permission gate, dynamic injection               │ named pipe
        ▲                                           │
        └────────── state/decision ─────────────────┤
                                                    ▼
                                  AutoLang.exe (agent, tray, resident)
                                  ├── LanguageDetector    (pure)
                                  ├── DecisionEngine      (pure)
                                  ├── ConversationStore   (%LOCALAPPDATA%\AutoLang)
                                  └── KeyboardLayoutService (Win32)
```

**Three deliberate departures from the PDR**, all load-bearing:

1. **The decision engine lives in the Agent, not the service worker.** It is what makes a future
   desktop signal source possible without a second implementation of the rules in another language,
   and it removes the MV3 service-worker eviction risk entirely.
2. **The native host is not a separate executable.** One `AutoLang.exe` runs as agent or as bridge
   depending on argv. Chrome spawns a fresh bridge per connection; it forwards over a named pipe to
   the resident agent. Merging them cut the published size from 211MB to 12.6MB.
3. **Broad site access is offered, never required.** `optional_host_permissions` holds `*://*/*`;
   `host_permissions` holds WhatsApp Web alone. The distinction is the whole permission story —
   in the optional list it means "the user may grant this", in the required list installing would
   grant it silently. `privacy-audit.mjs` fails the build if a broad pattern moves.

**Layout switching:** `PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)`. wParam **must be 0** —
`FORWARD`/`BACKWARD` mean "cycle" and make Windows ignore the explicit HKL. Layouts are resolved by
LANGID against `GetKeyboardLayoutList` at runtime, never by hardcoded KLID: this machine has Hebrew
as `0002040D`, not the `0000040D` the PDR assumed.

**Privacy by construction:** the content script counts letters and discards the text inside one
function. `MessageStats` is what crosses every boundary — there is no field anywhere in the wire
contract that could carry message content. Conversation identity is a salted SHA-256, hashed in the
page; the Agent **refuses** any key that is not 32 lowercase hex characters. Password, payment-card
and one-time-code fields are excluded before anything is read at all.

### How a generic site works without a second engine

A page has no message history and no direction, so two of the engine's five evidence sources are
unavailable. It never needed them. The mapping is exact and `DecisionEngine.cs` is untouched:

| Engine source | On WhatsApp | On any other site |
|---|---|---|
| Manual pin | per chat | per writing box |
| Conversation memory | what you typed there | **the same, and it is the main path** |
| Outgoing messages | your sent messages | *(unused)* |
| Weak fallback (incoming) | the other person's words | **the language of the surrounding text**, and only when there is enough of it to mean anything |
| Global default | — | the same |

Two rules decide what "the surrounding text" is, and both were paid for on Google Sheets: only
text the user can actually see is counted, and below sixty visible letters the adapter reports no
evidence rather than thin evidence. A ratio over a handful of letters is returned as certainty,
which is how an interface came to outvote a document.

The page's language enters as *incoming* because that is what it is: words the user did not write.
The engine already holds incoming evidence to a higher bar and already refuses to commit it to
memory. Both rules were paid for once, in the defect below, and they apply here unchanged.

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

**360 tests: 142 Core, 96 Agent, 122 TypeScript.** The build fails on any of them, on a privacy
audit failure, or on a store preflight failure.

Count them rather than adding them up from a previous message. Three commits state a total that is
one or ten short, every one of them mine and every one an arithmetic slip in prose while the code
was right.

Verbose logging is off by default and is the only way to see decisions:

```powershell
Get-Process AutoLang | Stop-Process -Force
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --verbose
```

Autostart does **not** pass `--verbose`, so it is quiet after the next sign-in.

Every decision line names the site it came from. Generic-site signals are recognisable by shape:
exactly one message, always incoming — `mine=0[...] theirs=1[...]`.

**Reloading the extension no longer requires reloading every tab.** Chrome severs the content
script in open pages and leaves it there inert; the service worker now revives them once per
extension load. This cost several diagnosis rounds, because the fix was first hung on
`chrome.runtime.onInstalled`, which does not fire for an unpacked reload. If a tab ever does go
quiet, its console says so.

---

## Defects found the hard way

Do not reintroduce these. Each has tests; the comments explain the reasoning at each site.

**The adapter matched every selector and understood nothing.** WhatsApp changed its DOM: the
`message-in`/`message-out` classes are gone and `data-id` no longer carries a JID. 49 green tests
said nothing because the fixtures described the 2024 DOM. Direction is now resolved by **geometry** —
bubble distance to panel edges, against `getComputedStyle(panel).direction`, because RTL mirrors and
in a Hebrew UI the user's own messages are on the **left**.

**The weak fallback was written to memory.** Incoming messages are the other person's language — a
hint worth acting on once, never worth recording, because memory outranks the user's own messages on
every later visit. This rule is now also what protects every generic site.

**The typing guard learned from the product's own output.** After an automatic switch the layout in
use is the last guess, so the moment the user began typing that guess became the conversation's
remembered language. One wrong switch became permanent. The guard declines to learn a layout the
engine imposed and the user has not touched since.

**Suppressed decisions dropped their source and confidence,** so a conversation read in full
reported source `None` at confidence `0.00` — indistinguishable from having seen nothing.

**The log recorded only applied switches,** which made "it did not switch" undiagnosable. It now
logs every decision with the evidence behind it, split by who wrote the messages.

### From making it work everywhere

All five were found by using the product, and none by a test. Each report was accurate and each of
my first hypotheses was wrong; the log settled every one.

**The popup hid the button it existed for.** Chrome withholds a tab's address from an extension
with no permission for that tab — which is every not-yet-granted site. The panel read the address,
got nothing, and hid itself, so "Use on this site" was invisible on exactly the sites it was for.
`activeTab` fixes it; the panel now says what it cannot read rather than disappearing, because
hiding silently is what turned a one-line bug into a long search.

**An English interface outvoted a Hebrew message.** Sampling the whole document read Gmail as
`he=1228 en=1642` — about 0.57 against a bar of 0.85 — so the engine correctly refused to decide
and the product did nothing. Reading now climbs to the container around the box and counts prose
only, skipping controls, labels and navigation. Raising the character threshold was the first
attempt and it broke on its own fixture: what separates a message from an interface is not how much
text there is but what kind.

**One `aria-label` served every reply box in Gmail,** so the whole site shared one memory and
answered with whatever had been typed last, in every thread, forever — memory outranks analysis by
design. Identity now includes the document: path and fragment, never the query string, where
transient input lives. Keys multiply, so entries past the memory TTL are dropped on the next write;
pins never are.

**"Has characters" is not "the user is typing."** A reply box opens with a signature in it, and the
typing guard held those messages shut permanently — nothing was ever going to remove the signature.
Emptiness is measured against what the field held on arrival, and clearing back to it, or sending,
opens the decision again.

**An extension reload leaves every open tab running a severed script.** It stopped itself correctly
but announced it only in that page's console, and the orphan check missed the shape where Chrome
removes `chrome.runtime` outright rather than throwing a named error — so the observer warned every
100ms forever. Both shapes are recognised now, the runtime is checked before use rather than after
failing, and open tabs are revived once per extension load. This is not a development annoyance:
every published update does it to every user with a tab open.

### Outside the browser

A second source, and the one that could most easily stop being a keyboard switcher. It reads
**nothing** in another application: reading contents means accessibility APIs, and noticing typing
means a global keyboard hook, which is a keylogger. `privacy-audit.mjs` fails the build if either
appears in the Agent's source.

It does not need to read anything, and Sheets is why we knew that before writing it. Identity is
the process plus the window title, salted-hashed in `DesktopIdentity` into the same 32-hex shape
every source must produce. Only applications the user adds are watched, and one that was never
added produces nothing at all — no decision, no key, not even a note that it was open.

**Three things this cost that were not obvious:**

`SetWinEventHook` reports arrivals. **Nothing reports a layout changed while the user stays put**,
because Windows sends that to the window rather than to an observer. Without a one-second re-read
of the current window, the desktop source can never learn anything.

**Arriving somewhere with a different layout is not a choice.** It is where the user came from.
Reading it as a choice makes Slack learn English every time it is reached from an English editor,
overwriting what was chosen on purpose. The engine tells them apart by whether the conversation
changed between two observations.

**A transition is not the only evidence.** Somebody who sets Hebrew and simply stays there never
produces one, and a live log showed `memory=none` on the same window once a second for as long as
they sat in it. So the layout in use now counts — but *only where nothing can be read*, which is
what `DecisionRequest.CanReadContext` is for. Three existing tests failed the moment that rule went
in without the qualifier, and each was right: on a web page, finding no evidence **is** evidence.

**A limit worth knowing before investigating it again:** some applications never change their
window title, so every conversation inside them is one context. Measured, not assumed:

| Application | Window title | What that gives |
|---|---|---|
| Claude | `Claude` | one context for the whole application |
| WhatsApp desktop | `WhatsApp` on the frame, `(10) WhatsApp` on its WebView | one context; the number is an unread count and would mint a new one per message |

This is the application's doing and there is no version of this product that fixes it. The chat name
lives in the interface, not the title, and reaching it means reading the window's contents — the one
thing the privacy audit fails the build over. Using the WebView's title instead is worse, not
better: the unread counter changes constantly.

**For WhatsApp, say so plainly: use WhatsApp Web.** The browser adapter reads the chat title from
the DOM, remembers per conversation, and analyses the messages. The desktop application can only
ever offer less, and a user reporting "it does not work in WhatsApp" is most likely describing this
rather than a fault.

**The anti-echo guard was global and had to be scoped.** It stops a conversation confirming its own
guess, but the layout it compared against was single state on an engine shared by every
conversation - so imposing Hebrew in one application made Hebrew look like our guess *everywhere*,
and the next application could not learn the language sitting in front of it. Found on WhatsApp,
from a log line reading `NoSignal` where nothing should have blocked. Single state on this engine
has now caused this class of fault three times; when something is refused for no visible reason,
check what is being compared against and whether it belongs to this conversation.

### Where there is nothing to read

Google Sheets took four rounds, and it is worth reading as one story rather than four fixes. Its
grid is drawn on a **canvas**, so the DOM holds no cell text at all. The product read the only text
there was — Google's interface — and switched to English at full confidence, over and over, while
the user typed Hebrew. Four times across five hours in one log.

**A manual change had never been noticed.** PDR section 18 asks the product to back off when the
user moves the layout themselves, the store listing promises it, and `NoteManualChange` had always
implemented it — but nothing called it. It was only ever inferred from the typing guard, which
needs a composer it can read. The engine now compares the layout against the one it saw last *in
the same conversation*; a value it did not set is the user, and that is learned as well as obeyed.
Two wrong versions came first and both are worth knowing: comparing against the last *imposed*
layout misses the case where the layout was already correct and nothing was imposed, and a detector
not scoped to one conversation blames whichever conversation arrives next and puts it in a cooldown
it never earned.

**The application's shell outvoted its content**, which is what sampling a whole document does in
an application. Reading climbs to the container around the box and counts prose only, skipping
controls, labels and landmarks.

**Then measuring the live page ended the guessing, and should have come first.** Every one of the
333 characters the sampler collected on a blank spreadsheet was *invisible*: a hidden error banner,
screen-reader hints, and the closed account panel — which is where the user's own name and email
were being counted from. Text is now counted only if it is visible. That is the correct rule and a
smaller footprint at once: what somebody is about to write is informed by what is in front of them.

**Twenty-nine letters were still enough**, because confidence is a ratio. The climb to a context
had a floor and the fallback to the body had none, so it took whatever was there. Below sixty
visible letters the adapter now reports no evidence at all rather than thin evidence, and the
engine's default leaves the keyboard alone.

The general lesson is in the order those arrived: four rounds of inference from a log, then one
measurement that corrected two of them. Two comments in the code had by then cited a live log for
things that were never observed — the fragment format, and why the cell editor had no name. Both
were wrong. If a page is behaving strangely, open it and measure it before writing down why.

**A caution about my own conclusions:** I have repeatedly over-concluded from one data point and
been corrected by the log — declaring the whole chain dead when the user's tabs simply held
orphaned scripts, and twice writing an invented observation into a comment. Check which process's
view you are reading, check the log before believing a hypothesis, and do not describe evidence you
have not looked at.

---

## Map

| | |
|---|---|
| `agent/AutoLang.Core/` | Pure, testable, no Windows dependency. Detector, decision engine, store, wire types |
| `agent/AutoLang.Agent/` | Win32. Layout service, pipe server, bridge mode, tray, `AgentCore` |
| `extension/src/adapters/whatsapp/` | Selectors, geometry-based direction, adapter. The fragile part |
| `extension/src/adapters/generic/` | Any other site: field selection, context climbing, field identity |
| `agent/AutoLang.Agent/ForegroundWatcher.cs` | The desktop source. Two window events and a one-second re-read |
| `agent/AutoLang.Core/DesktopIdentity.cs` | Window title to salted hash. The only way a desktop key is made |
| `extension/src/background/` | Permission gate, dynamic injection, tab revival, native port |
| `extension/src/content/` | Observer: adapter chain, debounce, focus, typing filter, orphan handling |
| `tools/verify-registration.ps1` | Is the registration visible to other processes? |
| `tools/review-log.ps1` | Flip-flop and memory-churn detector. Proven against a fixture |
| `tools/privacy-audit.mjs` | Release gate. Proven to catch a planted phone number |
| `tools/store-preflight.mjs` | Derives the extension ID from the manifest key |
| `docs/ACCEPTANCE.md` | The rows, what passed, and what each round of manual testing found |
| `docs/STORE_LISTING.md` | Listing copy, permission justifications, launch order |

**Extension ID `iblcjhakhfggopgijnankilmifbjbdbp`**, fixed by the `key` field in the manifest. The
native host allowlist names it exactly. Remove `key` and every install fails while the extension
looks perfectly healthy — `store-preflight.mjs` recomputes the ID from the key to catch that.

---

## Open

**Manual E passed in full on 2026-09-04**, all eleven rows read back from the agent log rather
than from what the screen appeared to do — and the product was overriding the keyboard on Google
Sheets the whole time. Four rows that would have caught it are written in `ACCEPTANCE.md` and have
not been run. Three things worth knowing:

- Row 4 is the strongest evidence the design works: two boxes on one Gmail page, two seconds apart,
  one switching to Hebrew from memory and the other to English from its own context.
- Rows 3 and 4 failed their first attempt for a reason that will catch the next person too. Typing
  in the language the engine already chose teaches it nothing — the anti-echo rule correctly refuses
  to learn its own guess, so everything read `memory=none` and it looked broken. Switch the layout
  by hand first. Treat any "it never remembers" report as this until proven otherwise.
- Every row assumed a page whose text is in the DOM. A canvas application has none, and no row
  asked what happens then. That is the gap the Sheets round came through. Four rows now cover it:
  **E12, E14 and E15 pass. E13 fails on Sheets and passes on Gmail**, which is the useful result:
  a manual change is not learned on Sheets because detection needs two consecutive observations of
  the same conversation and focus moves between eight fields there, while a Gmail compose box holds
  focus and the whole sequence fires — ManualChange, then memory, then a ManualCooldown line
  refusing to switch back. The guard is not loosened to turn the Sheets row green; a stability test
  proved what loosening costs.

**CPU and memory: settled, and cheap.** 31ms of CPU across 8.8 minutes of active use; 2.594s total
across 12.9 hours of uptime, or 0.006%. Memory stable at 15.1MB private (46MB working set, which
counts shared runtime pages). The generic adapter attaches no MutationObserver and reacts only to
focus and typing, and the numbers say that was the right call.

**Manual F: six of seven pass.** F19 - two windows of one application remembering separately -
could not be run, because the application used never changes its window title. It needs one that
does. Everything else passed with evidence, including that no window title reaches disk and that
the watcher never decides anything about a browser process.

**Alt-tab isolation: settled.** It never needed staging — it happened 23 times on its own during a
day's use, every one `Suppressed blocker=NotForeground`, none followed by a switch, against 212
switches applied that session. Two independent locks: the engine refuses before choosing, and
`KeyboardLayoutService.Switch` re-reads the foreground window and refuses to post the message.

**Untested by choice:** the Windows setting "let me use a different input method for each app
window" in its OFF state.

**A risk, not a defect:** requesting `*://*/*` even as an optional permission will draw a harder
store review than a single host would. The justification in `STORE_LISTING.md` is written and
honest. It remains the field most likely to decide how long approval takes.

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
accurately, and has been right every time I doubted them — *"it works once when I refresh the
page"* was a complete description of orphaned content scripts, and I read past it twice before the
log made it obvious. Take the reports literally and go to the log.
