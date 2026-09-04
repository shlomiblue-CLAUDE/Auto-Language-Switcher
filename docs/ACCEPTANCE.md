# Acceptance

PDR section 16 lists twelve acceptance rows. Nine are settled automatically and re-checked on every
build. Three cannot be, and this document is mostly about those three — because a suite that
appears to cover everything is worse than one that says what it does not.

Run the automated part with:

```powershell
.\build.ps1
```

```bash
dotnet test                       # 201 tests, including the twelve rows below
node tools/privacy-audit.mjs      # the privacy gate
node tools/bridge-smoke-test.mjs  # five checks across three processes
```

---

## The twelve rows

| # | Row | Expected | How it is checked |
|---|---|---|---|
| 1 | עברית ברורה | 10 outgoing Hebrew → HE | `Clear_Hebrew_ten_outgoing_Hebrew_messages_gives_HE` |
| 2 | אנגלית ברורה | 10 outgoing English → EN | `Clear_English_ten_outgoing_English_messages_gives_EN` |
| 3 | הצד השני בשפה אחרת | incoming HE + outgoing EN → EN | `The_other_side_writes_another_language_...` |
| 4 | שיחה מעורבת | close scores → no change | `A_mixed_conversation_with_close_scores_changes_nothing` |
| 5 | אין טקסט מספיק | emoji/numbers only → no change, or saved preference | two tests, both outcomes |
| 6 | חזרה לשיחה מוכרת | saved preference → immediate switch | `Returning_to_a_known_conversation_...` |
| 7 | הקלדה פעילה | composer not empty → no switch | `Active_typing_...` |
| 8 | override | Always Hebrew beats English text | `Override_Always_Hebrew_beats_English_text` |
| 9 | שפה שלישית ב-Windows | direct selection, never cycling | `A_third_Windows_language_is_never_reached_by_blind_cycling` |
| 10 | שינוי DOM | selector failure → status, no wrong switch | two tests, plus **manual A** |
| 11 | פרטיות | no content in storage or traffic | `privacy-audit.mjs`, plus **manual B** |
| 12 | מיקוד | background tab must not switch another window | `Focus_an_event_from_a_background_tab_...` |

All in [`tests/AutoLang.Agent.Tests/AcceptanceTests.cs`](../tests/AutoLang.Agent.Tests/AcceptanceTests.cs),
each named after its row.

### What the automated rows do not prove

They run against the real decision engine with a **fake Windows**. That is the right trade for
what they cover — the decision logic — and it is exactly the wrong instrument for three questions:

- whether Windows actually applies a switch (settled once, in [SPIKE_RESULTS.md](SPIKE_RESULTS.md))
- whether the selectors match today's live WhatsApp (**manual A**)
- whether the browser truly makes no network requests (**manual B**)

---

## Manual A — the selectors match live WhatsApp

The DOM fixtures are synthetic. They prove the adapter parses, orders and classifies correctly;
they cannot prove the selectors still match a page WhatsApp redesigns without notice.

1. Open `https://web.whatsapp.com` and select a conversation
2. F12 → Console (Chrome may want you to type `allow pasting` first)
3. Paste [`tools/whatsapp-selector-probe.js`](../tools/whatsapp-selector-probe.js)

Expected verdict: `HEALTHY - all preferred selectors matched`.

| Verdict | What it means |
|---|---|
| HEALTHY | Every selector matched its preferred form |
| DEGRADED | Running on fallbacks. Still works; update `selectors.ts` before the fallbacks run out |
| BROKEN | Required selectors missing. The adapter will refuse to decide — correctly, but the product does nothing until fixed |

The output carries only tier numbers and letter counts, with phone numbers reduced to their shape,
so it is safe to paste into a bug report.

**Repeat after every WhatsApp redesign.** This is the one part of the product with an external
dependency that changes without warning.

---

## Manual B — no network traffic

The audit proves no network API appears in the shipped bundle. This proves nothing is sent at
runtime.

1. `chrome://extensions` → Auto Language Switcher → **service worker**
2. Network tab, then **Preserve log**
3. Use WhatsApp normally for a few minutes: switch conversations, type, pin one, pause the site

Expected: **zero requests**. Not "only requests to our domain" — zero.

Then confirm what reached disk:

```powershell
type %LOCALAPPDATA%\AutoLang\*.json
node tools\privacy-audit.mjs
```

The audit now has files to inspect, so its stored-data check runs instead of skipping. Every
conversation key must be 32 hex characters.

---

## Manual C — thirty consecutive switches

`StabilityTests` simulates this with a controlled clock, which catches loops but cannot catch a
window-focus problem or CPU burn. Both need a real session.

Set up two conversations, one where you write Hebrew and one where you write English. Alternate
between them thirty times, typing a few words in each.

Watch for:

| | Expected |
|---|---|
| The layout | Follows the conversation, within about half a second |
| While typing | Never changes mid-sentence |
| Flip-flopping | Never. Not once |
| Another window | Alt-tab away mid-session — its layout must not change |
| `AutoLang.exe` CPU | At rest between switches. Not a steady percentage |
| Memory after 30 | Stable, well under 50MB |

Task Manager, Details tab, watch `AutoLang.exe`.

Start the Agent with logging first, so the run leaves evidence rather than an impression:

```powershell
Get-Process AutoLang | Stop-Process -Force
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --verbose
```

Afterwards:

```powershell
type %LOCALAPPDATA%\AutoLang\agent.log
type %LOCALAPPDATA%\AutoLang\conversations.json
```

Expected: one entry per conversation used, each a 32-character hash carrying the language actually
typed there. Anything less is a real defect in conversation memory — PDR section 5 puts it second
in the priority order, above the message evidence itself.

### Read this from an ordinary shell

Do not inspect the store from a sandboxed or automated shell. It can be handed a private copy of
these files, exactly as it is handed a private copy of the registry, and the copy is stale.

That produced a false alarm during Manual B: the store appeared to hold a single conversation, and
that one was `9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7`, the fixture `tools/bridge-smoke-test.mjs` sends. It
looked like nothing real was being learned. Read through a process outside the sandbox, the same
store held six real conversations with the right languages, and the log showed the switches that
produced them at 11–21ms each.

The lesson is the one `tools/verify-registration.ps1` exists for: a file read is not evidence unless
you know which process's view you are reading.

### What the first run found

Two defects, both reported by the user as one symptom: it started well, got worse, and clearing the
store fixed it. Neither was visible to any automated test, and neither would have been found without
the log.

**The all-messages fallback was written to memory.** Incoming messages are the other person's
language — a hint worth acting on once, and never worth recording, because memory outranks the
user's own messages on every later visit. A conversation where the other person writes Hebrew and
the user answers in English learned Hebrew once and answered Hebrew forever.

**The typing guard learned from the product's own output.** It records the layout in use while the
user types, which is sound, but it did not ask where that layout came from. After an automatic
switch the layout in use is the last guess; the moment the user began typing, that guess was written
back as the conversation's language. One wrong switch became permanent. A log showed one
conversation's memory flipping Hebrew, English, Hebrew inside thirty seconds along this path.

The instrument mattered as much as the fixes. The log recorded applied switches and nothing else, so
a session with four conversation changes and no switches produced no lines at all — suppressed,
already-correct, and no-decision were indistinguishable from silence. It now records every decision
with the evidence behind it, split by who wrote the messages, which is what separated "genuinely
mixed conversation" from "direction detection failed" in one reading.

### The second run

Clean. No flip-flop: one conversation held its language across ten consecutive reads where it
previously oscillated within seconds. Two switches two seconds apart turned out to be two different
conversations, which is correct. Switches at 11–19ms from three different evidence sources, no
failed switch, no hysteresis block.

Not observed on this run: CPU and memory under load, and the alt-tab check that another window's
layout is left alone. Those rows are still open.

---

## Manual D — Edge in the foreground ✅ done 2026-09-03

7/8 transitions, `foreground=True`, 15–25ms — identical to Chrome, including which strategy fails.
The shipped binary then applied a real switch to that same window:

```json
{"outcome":"Switch","language":"he-IL","source":"OutgoingMessages","applied":true}
```

To repeat it, the spike can raise Edge itself:

```powershell
cd "D:\claude workspace\H-E\spike\host"
.\bin\Debug\net8.0-windows\AutoLangSpike.exe matrix --activate msedge --delay 1
```

`--activate` forces the window forward with a synthetic ALT press, because Windows refuses
`SetForegroundWindow` from a process that does not already hold focus — `AttachThreadInput` alone
was not enough either. **The product must never do this**; it lives in the spike, which is
diagnostic code. A keyboard switcher that grabs focus would be worse than the problem it solves.

---

## Manual E — an ordinary site

The generic adapter has no selectors to break, so nothing here is about parsing. What cannot be
proven from a fixture is that the permission model behaves the way the listing says it does, and
that a page nobody has looked at does not cost CPU.

Rows 1–8 on a site that is not WhatsApp — Gmail is a good one, because it has a compose
box and a search box on the same page.

| # | Do this | Expected |
|---|---|---|
| 1 | Open the site before allowing it. Popup → "This site" | Says it is not being watched. Nothing in the service worker console |
| 2 | Click **Use on this site**, accept Chrome's prompt | Starts working in the tab that is already open, with no reload |
| 3 | Type Hebrew in the compose box. Go to another tab and come back | Layout returns to Hebrew on focus |
| 4 | Type English in the search box on the same page | It remembers English there, and Hebrew still in compose |
| 5 | Focus a password field on any login page | Nothing in the log. No decision, no key, no signal at all |
| 6 | Click **Stop using this site** | The log goes quiet for that site immediately, without reloading the tab |
| 7 | Reload the page after revoking | Still quiet |
| 8 | Hold a key down in a text box for a few seconds | One decision in the log, not one per character |
| 9 | Click **Use on all sites**, accept | Any site works with no further asking, including ones never visited before |
| 10 | With all sites on, open WhatsApp | **One** decision per conversation change in the log, not two |
| 11 | Click **Stop using all sites** | Back to per-site. Sites granted individually before are unaffected |

Row 10 is the one that would go unnoticed. The all-hosts pattern covers WhatsApp too, so the
declared content script and the dynamically registered one can both run in the same page —
two observers, every signal sent twice, and the engine taught the same thing twice. The
registration excludes the declared match to prevent it, and the log is where a failure shows.

Row 8 is the one worth being suspicious about. Typing is an event per character, and every signal
that carries a learned layout is a write to `conversations.json`; the log is where a filter that
silently stopped working would show up as a wall of identical lines.

Read the evidence, not the impression:

```powershell
.\tools\review-log.ps1 -Isolated
```

### Run of 2026-09-04

Performed by the user; each row read back from the agent log rather than from what the screen
appeared to do. Rows expecting silence are only marked where the user confirmed they took the
action, because silence is otherwise indistinguishable from a step not taken.

| # | Result | Evidence |
|---|---|---|
| 1 | PASS | Popup read exactly "Not watching duckduckgo.com yet." |
| 2 | PASS | A decision for `duckduckgo.com` appears seconds after the grant, with no page reload between them. Injection into an already-open tab works |
| 3 | PASS | After a manual switch to Hebrew and typing, `a6843237` shows `memory=Hebrew`; returning to the empty box gives `Switch lang=Hebrew src=ConversationMemory` |
| 4 | PASS | Two boxes on one page holding different languages two seconds apart: `a6843237` → Hebrew from memory, `0d63a6a5` → English from its own context. Moving between them flips the layout each way |
| 5 | PASS | A password field was focused and typed into. Not one line in the log — no decision, no key, no signal |
| 6 | PASS | Silence from that site immediately after revoking, with the tab never reloaded |
| 7 | PASS | Still silent after the reload |
| 8 | PASS | A key held for several seconds produced **one** `UserTyping` line. Two exist in the whole session |
| 9 | PASS | `volfr.com`, never granted individually, produced decisions immediately after the all-sites grant and was never asked about |
| 10 | PASS | Five conversation changes, **one** Switch each, and no two decisions on the same key within 60ms. Two observers in one page would arrive in near-lockstep; nothing does |
| 11 | PASS | Gmail was granted individually, then the all-sites grant was revoked at 13:26. Gmail was still deciding at 13:44. Chrome did not absorb the narrower grant into the broad one |

**Testing memory needs a language the product did not choose,** and the first attempt at rows 3
and 4 proved nothing because of it. The user typed English into boxes the engine had already set
to English; the anti-echo rule then correctly declined to learn, because the layout in use was its
own guess rather than a choice. Every read came back `memory=none` and it looked like a failure.

Switching the layout by hand first and typing in the language it did *not* pick makes the same rows
pass immediately. Anyone re-running these needs to know that, and any bug report of the form
"it never remembers anything" should be checked against it before it is believed.

### What passing eleven rows did not prove

All eleven passed, and the product was at that moment overriding the user's keyboard on Google
Sheets — switching to English at full confidence while they typed Hebrew, four times across five
hours. Every row above was written against a page whose text is in the DOM. A canvas application
has none, and nothing here asked what happens then.

The rows that would have caught it, and that a future round should run:

| # | Do this | Expected | Run of 2026-09-04 |
|---|---|---|---|
| E12 | Open a Google Sheet, click a cell and type | No decision at all. The log stays quiet for that site | **PASS** — every empty-composer read returns `NoSignal` on evidence of `he=0 en=0`. The adapter reports nothing at all |
| E13 | Switch the layout by hand mid-typing, keep typing | It is not switched back. The next line reads `ManualChange`, then `memory=` the language chosen | **FAIL** — see below |
| E14 | Move between cells, the formula bar and the name box | Focus moves between several fields; none of them may switch the layout under a field being typed in | **PASS** — eight keys interleave exactly as they did when this was broken, and the layout holds Hebrew across all of them |
| E15 | Any page: open a menu that contains text in another language | The menu's text is not counted while it is closed | **PASS** — measured on the live page: 13 characters counted with the Insert menu closed, and the same 13 with it open |

**58 decisions, zero switches applied.** That is the result these four rows exist for.

E14 is the one that would have found the original fault. Sheets has eight writing fields, focus
flits between them, and an empty one was deciding for the whole keyboard while the user typed in
another.

E15 passed twice over, by two independent guards: a closed menu is invisible, and an open one is
`role="menu"` and therefore application furniture.

### E13 fails, and it is recorded rather than fixed

The log shows the manual change happening — `layout=English` at 20:02:29 becomes `layout=Hebrew` at
20:02:32 — with no `ManualChange` line and `memory=none` throughout. It was not learned.

The reason is structural. Detection compares two consecutive observations *of the same
conversation*, and on Sheets focus moves between fields between one observation and the next. That
condition is not incidental: without it a stability test showed the change being attributed to
whichever conversation arrived next, putting it into a five minute cooldown it never earned. The
row is left failing rather than the guard loosened to make it green.

The consequence here is benign, and only here. Sheets produces no evidence at all, so nothing
competes with the user's choice and it survives by default rather than by memory. On a page that
does produce evidence the field is stable and detection fires — which is where it is needed. Worth
re-running E13 on Gmail, where it should pass, to confirm the failure is about Sheets and not about
the mechanism.

Worth stating plainly, because it is the general lesson: these rows check that the product does the
right thing where it can read the page. They do not check what it does where it cannot, and "cannot
read" is not rare — it is every canvas application, every custom editor, every remote desktop in a
tab.

**CPU and memory — the row left open since manual C.** 105 samples at 5s intervals across 8.8
minutes of active use, read from a process outside the sandbox:

```
CPU      2.563s → 2.594s   =  31ms over 8.8 minutes of use
         2.594s total over 771 minutes of uptime  =  0.006%
memory   46.2 → 46.4 MB working set, peak 46.8 MB;  private bytes 15.1 MB
```

At rest between switches, which is what the row asks. On the memory bar, honestly: 46.8 MB is not
"well under 50" — but that is working set, which counts shared runtime pages. What this process
actually costs is the 15.1 MB of private bytes.

---

## Results

Fill in when run. An empty row is more useful than an assumed one.

| Check | Date | Result | Notes |
|---|---|---|---|
| Automated suite (201 tests) | 2026-09-03 | PASS | |
| Privacy audit | 2026-09-03 | PASS, 1 skipped | Store empty; stored-data check did not run |
| Bridge smoke test (5) | 2026-09-03 | PASS | Against the installed build |
| Manual A — selectors | 2026-09-03 | FAIL, then PASS | Direction was dead; adapter v2.0.0 resolves 19/19 live, 0 disagreements |
| Manual B — network | 2026-09-03 | PASS | Zero requests in the service worker Network panel across several conversation switches. On disk: 3 files, no message text, the one stored key is 32 hex characters |
| Manual C — 30 switches | 2026-09-03 | FAIL, then PASS | Found two real defects. After both: no flip-flop across a session, switches 11–19ms, three evidence sources, zero failed switches. CPU and memory not observed |
| Manual D — Edge foreground | 2026-09-03 | PASS | 7/8, 15–25ms; real switch applied by the shipped binary |
| Manual E — an ordinary site | 2026-09-04 | PASS, 11/11 | Every row read back from the agent log. CPU 31ms across 8.8 minutes of use, 0.006% over 12.9 hours; memory stable at 15.1MB private. Rows 3 and 4 needed a second attempt. **All eleven passed while the product was overriding the keyboard on Google Sheets** — see what passing did not prove |
| Manual E12–E15 — a canvas application | 2026-09-04 | 3/4 PASS | 58 decisions, zero switches applied. E13 fails on Sheets for a structural reason, recorded rather than fixed |

---

## Definition of done (PDR section 17)

- [x] Extension loads in Chrome and Edge without errors
- [x] Native host installed and talking to both browsers
- [ ] Switching between two real WhatsApp conversations works consistently — **manual C**
- [x] Manual override and pause work and survive a restart
- [x] All unit and integration tests pass
- [x] No message text in logs, storage or traffic — audited, **manual B** confirms at runtime
- [x] README covers install, removal, troubleshooting and permissions
- [ ] Thirty consecutive switches with no loop or focus loss — **manual C**
