# Auto Language Switcher

Switches your Windows keyboard layout to the language you are about to type in.

You keep a supplier chat in English and a family chat in Hebrew. Windows does not remember which is
which, so every switch between them costs an `Alt+Shift` — and you find out you forgot only after a
line of gibberish. This reads which language you write in each conversation and sets the layout
before you start typing.

Everything happens on your computer. There is no server, no account, and no network request of any
kind. See [PRIVACY.md](docs/PRIVACY.md), which cites the code for each claim.

**Status:** working end to end. The pipeline moves a signal from the page to Windows and the layout
changes; the adapter reads direction correctly on live conversations as of 3 September 2026. Not
yet published to the Chrome Web Store.

## How it decides

It asks one question: *what language is this person about to type here?* Not what language the
conversation is in — what **you** write in it. So your own recent messages count for far more than
what you receive, and Hebrew typed in Latin letters ("ma nishma") correctly resolves to English,
because that is which keys you are pressing.

In priority order:

1. **You pinned it** — beats everything
2. **What you were typing with last time** — ground truth about your keyboard, not an inference
3. **Your recent messages** — up to 10, newer weighted higher
4. **The conversation as a whole** — a weak hint, needing a wider margin
5. **Your default** — only when nothing else says anything

Below 70% confidence it does nothing. Doing nothing costs one keystroke; switching wrongly
mid-sentence costs a deleted line and your trust.

It also refuses to act while you are typing, while the browser is in the background, within 750ms
of the last switch, and for five minutes after you override it.

## Install

Requires Windows 10/11 and both Hebrew and English layouts already added in Windows.

```powershell
.\build.ps1
.\installer\Install.ps1
```

Then load `extension\dist` unpacked at `chrome://extensions`. Full walkthrough:
[INSTALL_WINDOWS.md](docs/INSTALL_WINDOWS.md).

## How it is put together

```
WhatsApp Web
  content script          reads the conversation, counts letters, discards the text
        │  counts only
  service worker          transport, no decisions
        │  native messaging
  AutoLang.exe            one 12.6MB executable, two roles
     ├─ bridge mode       when Chrome launches it (the extension origin in argv says so)
     └─ agent mode        resident: decision engine, preferences, Win32 layout switching
```

Two design choices worth stating, both deviations from the original spec:

**The decision engine lives in the agent, not the browser.** Chrome evicts service workers
aggressively, and a desktop signal source added later must reach the same engine and the same
per-conversation memory without reimplementing the rules in another language.

**One executable, two roles.** Chrome spawns a fresh host process per connection and kills it when
the port closes, so the host can never be the resident agent — but it need not be a separate build.
A native messaging manifest has nowhere to put arguments, so the role comes from the extension
origin Chrome passes in argv.

| | |
|---|---|
| [`agent/AutoLang.Core`](agent/AutoLang.Core) | Detector, decision engine, store, wire contract. Pure, no Windows. |
| [`agent/AutoLang.Agent`](agent/AutoLang.Agent) | Win32 layout switching, named pipe server, tray icon, bridge mode. |
| [`extension`](extension) | MV3 extension. All WhatsApp DOM knowledge in one file. |
| [`tools`](tools) | Selector probe, icon generator, end-to-end smoke test. |

## Developing

```powershell
.\build.ps1                    # test, build, publish, package
.\build.ps1 -SkipTests
```

```bash
dotnet test                                    # 201 C# tests
cd extension && npm test && npx tsc --noEmit   # 49 TypeScript tests
node tools/bridge-smoke-test.mjs               # 5 checks across three processes
node tools/privacy-audit.mjs                    # the release privacy gate
```

The letter counter exists twice, in C# and TypeScript, because privacy requires the browser to
count locally rather than send text anywhere. [`tests/fixtures/detector-corpus.json`](tests/fixtures/detector-corpus.json)
is run by both suites so they cannot drift apart.

## Adding a language

Hebrew and English ship today. Adding Russian or Arabic is one row in
[`ScriptTable.cs`](agent/AutoLang.Core/ScriptTable.cs), the matching row in
[`languages.ts`](extension/src/shared/languages.ts), and corpus cases so the two stay in step.

## What is not done

- Selector drift is the standing risk. WhatsApp changed its DOM in September 2026 and the adapter
  was rewritten against it ([`docs/WHATSAPP_DOM_2026-09.md`](docs/WHATSAPP_DOM_2026-09.md)); run
  [`tools/whatsapp-selector-probe.js`](tools/whatsapp-selector-probe.js) after any redesign.
- Not code-signed, so the installer trips SmartScreen.
- Not in the Chrome Web Store.

## Documentation

[Install](docs/INSTALL_WINDOWS.md) · [Privacy](docs/PRIVACY.md) ·
[Troubleshooting](docs/TROUBLESHOOTING.md) · [Acceptance](docs/ACCEPTANCE.md) ·
[Phase 0 spike results](docs/SPIKE_RESULTS.md) ·
[Store listing](docs/STORE_LISTING.md)
