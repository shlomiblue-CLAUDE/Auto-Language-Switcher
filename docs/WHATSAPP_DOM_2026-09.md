# WhatsApp DOM findings — 3 September 2026

Run of manual acceptance A against a live, logged-in WhatsApp Web session.

**Verdict when first run: the adapter was broken against today's WhatsApp.** It collected zero
usable messages and the product would have sat at `NoSignal` forever. It failed safely — nothing
switched wrongly — but nothing worked either.

This is exactly what the probe existed to find, and it found it on the first real run.

**Fixed the same day.** Adapter v2.0.0 resolves direction by geometry. Verified live across 19
messages in a real conversation containing both directions: 19 of 19 resolved, zero unknown, and
the three signals agreed everywhere they overlapped — 16 cross-checks against the tail and 1
against the sender label, with zero disagreements. The rest of this document records what was
found and why the replacement works.

---

## What still works

| Selector | Matched | Tier |
|---|---|---|
| `mainPanel` | `#main` | 0, preferred |
| `conversationHeader` | `#main header` | 0, preferred |
| `messageRow` | `div[data-id]` | 0, preferred |
| `messageText` | `span.selectable-text span` | 0, preferred |
| `composer` | `#main footer div[contenteditable="true"]` | 0, preferred |

Text extraction is fine: the sample of 7 messages yielded 119 Hebrew and 15 Latin letters.

---

## What broke

### 1. Direction detection fails completely — the serious one

```
directions: { outgoing: 0, incoming: 0, unknown: 7 }
```

Both signals the adapter relies on are gone:

**`message-in` / `message-out` classes no longer exist.** Not renamed — absent. A search of every
element under `#main` for any class beginning with `message` returns an empty list.

**`data-id` no longer carries direction.** The old format was `true_<jid>_<msgid>`, where the
leading boolean *was* the outgoing flag and the middle segment was the chat JID. Today it is just
the message id — no underscores, no prefix, no JID:

```
data-id      3A7744F336117B4EF113
data-testid  conv-msg-3A7744F336117B4EF113
```

Since `directionOf` returns null rather than guessing, every message is skipped. The fail-closed
design worked; there is simply nothing left for it to read.

### 2. Conversation identity lost the JID

`chatIdFromRow` splits `data-id` on `_` and takes the middle segment. With no underscores it
returns null, so identity falls back to the header title — a contact name, which changes when a
contact is renamed and silently orphans the saved preference.

### 3. `messageList` degraded to a fallback

`#main div[role="application"]` no longer matches. It fell through to `#main div.copyable-area`,
which still works. Not urgent, but the preferred selector is stale and the fallbacks are finite.

---

## What replaces them

Three candidate direction signals, established by measurement rather than by naming.

### A. Geometry — recommended

WhatsApp *defines* an outgoing message as one drawn on the user's side. Measuring which side a
bubble sits on reads that definition directly, in any language, on every message.

Measured against a conversation panel with `direction: rtl`:

| row | side | gap left | gap right | is "you" | tail |
|---|---|---|---|---|---|
| 0 | left | 68 | 1158 | yes | in |
| 1 | left | 68 | 1237 | yes | none |
| 2 | right | 1234 | 62 | no | out |
| 3 | left | 68 | 1212 | yes | in |
| 4 | left | 68 | 1115 | yes | in |
| 5 | left | 68 | 1040 | yes | none |
| 6 | right | 1126 | 62 | no | out |

In RTL the user's own messages sit at the inline-end, which is the left. In LTR it is the right.
Comparing the bubble's distance to each edge of the panel and resolving against
`getComputedStyle(panel).direction` gives the answer without a locale table.

Cost: `getBoundingClientRect` forces layout. For ten bubbles behind a 100ms debounce that is
negligible, but it must not be done per mutation.

### B. The sender marker — locale-dependent

Every row carries exactly one `<span aria-label="…:">`. For the user's own messages it reads
`את/ה:` in a Hebrew UI, `You:` in English. Reliable, present on every message, and it agreed with
geometry on all seven rows — but it needs a string per supported UI language, and getting it wrong
inverts the product's central signal.

### C. The bubble tail — insufficient, and named backwards

`[data-testid="tail-out"]` and `tail-in` exist, but appear only on the first message of a group:
five of seven rows here. Worse, the names are inverted relative to the old convention —
**`tail-out` marked the other person's messages**, agreeing with geometry and with the sender
marker on every row. Presumably the name describes the tail's visual direction, not the message's.

Usable only as a tie-breaker, and only with that inversion written down.

---

## Other stable hooks now available

`data-testid` is used far more widely than before, and these look like better anchors than
structural guesses:

```
conversation-panel-messages     the scroll container - better than div.copyable-area
conversation-header
conversation-compose-box-input  the composer
msg-container                   the bubble, one per row
msg-meta                        timestamp and receipts
conv-msg-<id>                   per message
```

---

## What needs to change

1. `directionOf` — geometry first, sender marker second, tail third. Drop the `true_`/`false_`
   prefix check and the `message-in`/`message-out` classes; neither exists.
2. `chatIdFromRow` — the JID is not in `data-id` any more. Find another stable per-conversation
   identifier, or accept the header title and document that a rename orphans the preference.
3. `SELECTORS.messageList` — promote `[data-testid="conversation-panel-messages"]`.
4. `SELECTORS.composer` — consider `[data-testid="conversation-compose-box-input"]` as tier 0.
5. Fixtures — `tests/fixtures/whatsapp-dom.ts` builds the old shape, so the suite is green against
   a DOM that no longer exists. Rebuild it from this document.
6. The probe — teach it to report *why* direction failed, not just that it did. It reported
   `conversationIdSource: data-id (preferred)` because a `data-id` attribute was present, while the
   value inside it was unparseable. Presence is not the same as usability.

---

## A privacy note on this session

The probe itself reports only tier numbers, counts and redacted shapes, and it behaved correctly.

The follow-up investigation was ad hoc, and one query returned a list of `aria-label` values that
included a contact name. Every query after that reported only whether a label matched the "you"
pattern, never its value, and no message text was read at any point. Recording it because a privacy
discipline that is only described is not a discipline — the probe is written the way it is
precisely so that investigation does not have to be improvised.
