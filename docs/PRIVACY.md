# Privacy

Short version: your messages are read in the page, counted, and thrown away. Nothing leaves your
computer. There is no server to send anything to.

This document is written to be checkable rather than reassuring. Every claim below names the file
that implements it or the command that proves it.

## What the product actually reads

To decide which keyboard you need, it reads the messages already displayed in the conversation you
have open, and counts letters by alphabet. It never scrolls, never loads history, and never looks
at conversations you have not opened.

The result of reading a message is a pair of numbers. Reading "מה נשמע אחי" produces
`{Hebrew: 9}` — not the words, not who sent them, not when.

- Reading and counting: [`extension/src/adapters/whatsapp/adapter.ts`](../extension/src/adapters/whatsapp/adapter.ts)
- The counter itself: [`extension/src/shared/text-normalizer.ts`](../extension/src/shared/text-normalizer.ts)

## What leaves the page

Only counts. The message format has no field that could carry text, a contact name or a phone
number, so a leak would require adding one first — a visible change to a reviewed contract.

```jsonc
{
  "type": "signal",
  "site": "web.whatsapp.com",
  "conversationKey": "9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7",  // salted hash, see below
  "composerEmpty": true,
  "messages": [ { "direction": "outgoing", "index": 0, "counts": { "Hebrew": 9 } } ]
}
```

Both ends of that contract: [`extension/src/shared/protocol.ts`](../extension/src/shared/protocol.ts)
and [`agent/AutoLang.Core/Wire.cs`](../agent/AutoLang.Core/Wire.cs).

## Conversation names and phone numbers

WhatsApp identifies a conversation by phone number. The product never stores one.

Before anything leaves the page, the identifier is hashed with SHA-256 and a random value generated
when you installed. The salt is what makes this worth doing: without it, a hash of a phone number
can be reversed by trying every number in a country, and the same contact would produce an
identical key on every machine on earth. With it, your preferences file is meaningless anywhere but
your own computer.

The Agent additionally **refuses** any conversation key that is not a 32-character hash. The
guarantee does not depend on the browser side behaving — a component that sent a raw phone number
would be rejected, not stored.

- Hashing: [`extension/src/shared/hash.ts`](../extension/src/shared/hash.ts)
- Refusal: `IsHashedKey` in [`agent/AutoLang.Agent/AgentCore.cs`](../agent/AutoLang.Agent/AgentCore.cs)
- Tests: [`tests/AutoLang.Agent.Tests/ConversationKeyTests.cs`](../tests/AutoLang.Agent.Tests/ConversationKeyTests.cs)

## Outside the browser

The product also works in applications you add yourself — Slack, an editor, anything with a window.
This is the part where a keyboard switcher could quietly become something else, so it is worth
being exact about what it does and does not do.

**It reads nothing.** Not the text in the window, not what you type. Reading another application's
contents needs accessibility APIs; noticing that you are typing needs a keyboard hook, which is a
keylogger. Neither is in the code, and `tools/privacy-audit.mjs` fails the build if either appears.

What it sees is the name of the window in front and its title, and what it learns is the keyboard
layout you chose there. Nothing else exists to learn from — which is why this works at all: the
same two paths that make it work on a Google Sheet, where the grid is drawn on a canvas and there
is no text to read either.

**Only applications you add.** Settings → Applications. One that you have not added produces
nothing: no decision, no stored key, not even a note that it was open. The list of running
applications shown in that picker is asked for while the page is open and never stored.

**The window title never reaches disk.** Slack puts a channel or a person in it, a mail client puts
a subject, an editor puts a path. It is hashed with SHA-256 and a salt generated on your machine —
the same treatment a WhatsApp chat title gets, producing the same 32-character key, which the Agent
refuses anything else for.

## What is stored, and where

`%LOCALAPPDATA%\AutoLang` — three small files you can open and read.

| File | Contents |
|---|---|
| `settings.json` | Your settings. No conversation data. |
| `conversations.json` | Hashed key to language, mode, and a timestamp. |
| `sites.json` | Which sites you have paused. |
| `apps.json` | The applications you allowed, by process name. |

A real entry, in full:

```json
{
  "9f2a4c8e1b3d5f7009f2a4c8e1b3d5f7": {
    "Mode": "Auto",
    "LastReliableLanguage": "Hebrew",
    "UpdatedAt": "2026-09-02T18:10:01Z"
  }
}
```

That is everything the product remembers about a conversation: you type Hebrew in it.

There is no log of messages. The debug buffer holds the last 200 decisions — outcome, confidence,
and which rule applied — and lives in memory only. Its record type has no field for a conversation
key or for counts, so it cannot be extended into one by accident.

## What is not there

- **No network requests.** Not analytics, not telemetry, not an update check. The extension has no
  network permission and the code contains no HTTP client. `grep -rniE "fetch\(|XMLHttpRequest|HttpClient|WebRequest|sendBeacon" extension/src agent` returns nothing.
- **No account.** Nothing to sign up for, nothing to sign in to.
- **No AI or external language service.** Language detection is a letter count against Unicode
  ranges, done locally in under a millisecond.
- **No site you did not choose.** Installing grants `https://web.whatsapp.com/*` and nothing else.
  Every other site is added by you from the popup — one origin at a time, or all sites at once if
  you prefer — through Chrome's own permission prompt. Withdrawing one stops the extension reading that site straight away — the
  service worker refuses signals from an origin that is no longer granted and tells the page to
  stop, rather than waiting for you to reload the tab.
- **No password or payment fields.** On every site, `input[type=password]`, payment-card fields and
  one-time-code fields are excluded before anything is read. No identity, no counts, no signal.

## Permissions, and why each one exists

| Permission | Why |
|---|---|
| `storage` | Holds the random salt used for hashing. Nothing else. |
| `nativeMessaging` | Talks to the Windows agent. A browser extension cannot change a keyboard layout; only a native program can. |
| `https://web.whatsapp.com/*` | Reads the open conversation. The only site granted at install. |
| `scripting` | Runs the same bundled script on a site you allowed, and stops it when you withdraw the site. No remote or generated code. |
| `activeTab` | Lets the popup name the site you are on, so it can offer to enable it. Limited to the tab in front, only while the popup is open. The `tabs` permission would have exposed every tab's address instead. |
| `optional_host_permissions: *://*/*` | **Offered, never taken.** Chrome grants an origin from this list only when you click, so installing grants none of it. |

`<all_urls>` is deliberately not requested. The distinction that matters is which manifest field
broad access sits in: in `optional_host_permissions` it means "the user may grant this", in
`host_permissions` it would be granted silently at install. `tools/privacy-audit.mjs` fails the
build if a broad pattern ever appears in the required list.

## Checking any of this yourself

```bash
# Every outbound request the extension makes, if any:
#   chrome://extensions -> service worker -> Network tab. It stays empty.

# Everything stored on disk:
type %LOCALAPPDATA%\AutoLang\*.json

# Search stored files for anything identifying:
findstr /i "972 @c.us http" %LOCALAPPDATA%\AutoLang\*.json
```

The last command returning nothing is the property that matters, and it is asserted by the test
suite as well: `Nothing_identifying_is_ever_written_to_disk` in
[`tests/AutoLang.Core.Tests/ConversationStoreTests.cs`](../tests/AutoLang.Core.Tests/ConversationStoreTests.cs).

## Deleting everything

The tray menu and the extension's settings page both offer **Clear stored preferences**, which
deletes all three files immediately.

Uninstalling keeps them, because uninstalling is often a reinstall. To remove them too:

```powershell
.\Uninstall.ps1 -RemoveData
```

## One honest limitation

The product reads what is on screen in order to count letters. That reading happens inside your
browser, in the same page WhatsApp already runs in, and the text is discarded in the same function
that counts it. But "the code does not keep it" is a claim about code — which is why the code is
readable, the contract has no field for text, and the stored files are three small JSON documents
you can open right now.
