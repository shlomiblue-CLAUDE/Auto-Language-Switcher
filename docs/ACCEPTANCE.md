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

## Results

Fill in when run. An empty row is more useful than an assumed one.

| Check | Date | Result | Notes |
|---|---|---|---|
| Automated suite (201 tests) | 2026-09-03 | PASS | |
| Privacy audit | 2026-09-03 | PASS, 1 skipped | Store empty; stored-data check did not run |
| Bridge smoke test (5) | 2026-09-03 | PASS | Against the installed build |
| Manual A — selectors | | | |
| Manual B — network | | | |
| Manual C — 30 switches | | | |
| Manual D — Edge foreground | 2026-09-03 | PASS | 7/8, 15–25ms; real switch applied by the shipped binary |

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
