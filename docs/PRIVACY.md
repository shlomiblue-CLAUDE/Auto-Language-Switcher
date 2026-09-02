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

## What is stored, and where

`%LOCALAPPDATA%\AutoLang` — three small files you can open and read.

| File | Contents |
|---|---|
| `settings.json` | Your settings. No conversation data. |
| `conversations.json` | Hashed key to language, mode, and a timestamp. |
| `sites.json` | Which sites you have paused. |

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
- **No access to other sites.** The extension requests `https://web.whatsapp.com/*` and nothing
  else. It cannot read any other tab, and adding a site would require a new permission you would
  be asked to approve.

## Permissions, and why each one exists

| Permission | Why |
|---|---|
| `storage` | Holds the random salt used for hashing. Nothing else. |
| `nativeMessaging` | Talks to the Windows agent. A browser extension cannot change a keyboard layout; only a native program can. |
| `https://web.whatsapp.com/*` | Reads the open conversation. The only site requested. |

`<all_urls>` is deliberately not requested, and each additional site will be added explicitly.

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
