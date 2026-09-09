# Chrome Web Store submission

Everything needed to submit, in the form the review asks for it. The two reviewed fields —
permission justifications and single purpose — are the ones that decide how long this takes, so
they are written as answers rather than notes.

**Reality check on timing:** this extension requests `nativeMessaging`, which routes it to manual
review. Days to weeks, not hours. Nothing in the build affects that; only the justification text
does.

---

## Listing

**Name** — `Auto Language Switcher`

**Summary** (132 char max)

> Switches your Windows keyboard to the language you write in each conversation. Everything is
> processed on your computer.

**Category** — Productivity
**Language** — English (Hebrew listing to follow)

**Description**

```
You keep a supplier chat in English and a family chat in Hebrew. Windows does not remember which
is which, so every switch between them costs an Alt+Shift — and you usually find out you forgot
only after a line of gibberish.

Auto Language Switcher reads which language YOU write in each conversation and sets your Windows
keyboard layout before you start typing.

HOW IT DECIDES

It asks one question: what language is this person about to type here? Not the language of the
conversation — the language of your half of it. Your own recent messages count for far more than
what you receive.

Below 70% confidence it changes nothing. Doing nothing costs one keystroke; switching wrongly
mid-sentence costs a deleted line.

IT STAYS OUT OF THE WAY

• Never switches while you are typing
• Never touches a window that is not in front
• Backs off after you change the layout yourself
• Pin any conversation to Hebrew or English — that beats everything
• Selects the layout directly, never cycling blindly through your languages

When it decides not to act, the popup says why in plain words.

NOTHING LEAVES YOUR COMPUTER

There is no server. No account, no telemetry, no update check — the extension holds no network
permission at all. Messages are counted in the page and discarded; what reaches the rest of the
product is a pair of numbers like { Hebrew: 9 }.

Conversation names are phone numbers, so they are never stored. They are hashed with a random
value created when you install.

WORKS WHERE YOU WRITE

WhatsApp Web works the moment you install it. Every other site is yours to turn on: open the
popup, click "Use on this site", and it starts remembering the language you type there — per
writing box, so a Hebrew message and an English search on the same page stay separate.

Write in a lot of places? "Use on all sites" is one click instead of one per site. Either way it
is your click, never something the install took, and either way it can be withdrawn from the same
button.

LANGUAGES

Hebrew, English, Russian, Arabic and Greek. It tells them apart by alphabet, which is why those
five and not more: each is written in a script of its own. Languages that share the Latin
alphabet — Spanish, French, German — cannot be distinguished this way, and the extension does not
pretend to. Nor can it tell Hebrew typed in Latin letters from English; "ma nishma" is English as
far as your keyboard is concerned, which is the right answer.

Settings let you narrow it to the languages you actually type, useful if you can read one you
never write in.

REQUIREMENTS

• Windows 10 or 11
• The Auto Language Switcher companion program — a browser extension cannot change a keyboard
  layout; only a native program can. Free, open source, and downloaded from:
  https://github.com/shlomiblue-CLAUDE/Auto-Language-Switcher/releases/latest
• The languages you want it to use, already added as Windows keyboard layouts

The companion program is not yet code-signed, so Windows may warn about it or quarantine it. The
download explains what that means and how to check it. The source is public:
https://github.com/shlomiblue-CLAUDE/Auto-Language-Switcher

```

---

## Permission justifications

These are the reviewed fields. Each answers "why is this necessary", which is the question actually
being asked.

**`nativeMessaging`**

> Changing a Windows keyboard layout requires a native program; no browser API can do it. The
> extension sends a companion Windows application a language code and nothing else. The connection
> is restricted to one named host, and that host accepts only this extension's ID.

**`storage`**

> Stores a single random value, generated at install, used to hash conversation identifiers before
> they leave the page. No message content, contact names or settings are kept in browser storage.

**Host permission — `https://web.whatsapp.com/*`**

> The extension counts letters by alphabet in the currently open conversation to determine which
> keyboard the user needs. This is the only host granted at installation.

**`scripting`**

> Used to run the same content script on a site after the user has granted permission for it, and
> to stop running there when they withdraw it. It injects one bundled script; no remote or
> generated code is ever executed.

**`activeTab`**

> The popup shows which site the user is on and offers to enable the extension there. Reading the
> address of the tab in front is the only way to name that site and to request permission for it.
> `activeTab` limits this to the tab the user is looking at, at the moment they open the popup —
> the alternative, the `tabs` permission, would give access to every tab's address all the time.

**Optional host permissions — `*://*/*`**

> Requested at runtime from a button in the extension's popup, and only when the user clicks it.
> The popup offers two forms: a single origin — the site currently open — or all sites, for users
> who write in many places and do not want to approve each one. Chrome's own permission dialog
> confirms either, and both can be withdrawn from the same button.
>
> On a granted site the extension identifies the text field the user is typing in and counts
> letters by alphabet to determine which keyboard they need. Password, payment-card and
> one-time-code fields are excluded and are never read. No page text is stored or transmitted —
> only per-alphabet counts, and only to the local companion program.
>
> This is offered rather than required precisely so that installation grants nothing: a user who
> only wants WhatsApp Web never grants another site, and the install warning stays limited to it.

**Remote code** — No. Everything is bundled; there is no `eval`, no remote script, no CDN.

---

## Single purpose

> Setting the Windows keyboard layout to match the language the user writes in the place they are
> about to type.

Everything in the extension serves that: reading the conversation or text field to determine the
language, sending the result to the companion program that performs the switch, and a popup for
overriding the decision and for choosing which sites it may run on.

---

## Data disclosure

Every box: **not collected**.

| Category | Collected |
|---|---|
| Personally identifiable information | No |
| Health, financial, authentication information | No |
| Personal communications | No |
| Location, web history, user activity | No |
| Website content | No |

The last one deserves a note in the review comments, because the extension does *read* page
content:

> The extension reads displayed text solely to count letters by Unicode range. The text is
> discarded within the same function; only per-language counts leave the page. Nothing is
> transmitted off the device — the extension has no network permission and makes no requests.
> This happens on WhatsApp Web and on any additional site the user has explicitly granted from the
> popup. Password, payment-card and one-time-code fields are excluded and are never read.

All three certifications apply: no selling data, no unrelated use, no creditworthiness use.

**Privacy policy URL** — required for approval, and live:

> https://shlomiblue-claude.github.io/Auto-Language-Switcher/privacy.html

**Support / contact** — the form asks separately, and the privacy policy points at the same place:

> https://github.com/shlomiblue-CLAUDE/Auto-Language-Switcher/issues

---

## Assets

| Asset | Size | Status |
|---|---|---|
| Store icon | 128×128 PNG | Ready — `extension/public/icons/icon128.png` |
| Screenshot 1 | 1280×800 | **Needed** — popup over a WhatsApp conversation, HE badge |
| Screenshot 2 | 1280×800 | **Needed** — popup showing "too mixed to call", the restraint |
| Screenshot 3 | 1280×800 | **Needed** — settings page |
| Small promo tile | 440×280 | Optional |

Screenshots must not show real contacts or message content. Use a test account with invented
conversations — a privacy claim undercut by its own screenshots is worse than no screenshots.

---

## Before submitting

`.\build.ps1` now runs `tools/store-preflight.mjs` and fails the build on anything mechanical:

```
node tools/store-preflight.mjs
```

| Checked for you | |
|---|---|
| `key` present, and the ID it derives | recomputed from the key the way Chrome does |
| That ID matches the native host allowlist | compared against `$ExtensionId` in `Install.ps1` |
| Manifest v3 | |
| Permissions still only `storage`, `nativeMessaging`, `scripting`, `activeTab` | anything else needs a justification above |
| Host access granted at install is still WhatsApp Web only | broad access must sit in `optional_host_permissions`, never in `host_permissions` |
| Declared icons exist at their declared pixel sizes | read from the PNG header, not the filename |
| No `.map` or `.ts` files in the package | |
| `extension.zip` is not older than the build | the quiet way to ship yesterday's version |

**The `key` field is the one that would have bitten.** Remove it and the store assigns a different
extension ID, the native host allowlist stops matching, and every install fails with
"agent not running" — while the extension itself looks perfectly healthy. That is why the preflight
derives the ID rather than comparing two hardcoded strings.

Still yours:

- [ ] Privacy policy live at a public URL
- [ ] `version` bumped in `extension/public/manifest.json`
- [ ] Selectors verified against live WhatsApp (`tools/whatsapp-selector-probe.js`)
- [ ] Screenshots taken from a test account
- [ ] Companion program downloadable from a public URL, linked in the description

---

## Edge Add-ons

Separate submission, same package, same justifications. Worth doing: the product supports Edge
already, and the review queue is usually shorter.

---

## Code signing — start this first

The longest lead time in the whole launch, and unrelated to any code.

Without a signing certificate, SmartScreen warns on the companion program's installer and most
people stop there. That makes it the gate on adoption, not the store listing.

Priced 2026-09-07:

| | Cost | Note |
|---|---|---|
| Azure Artifact Signing, Basic | $9.99/month | No hardware token. **Individual validation is US and Canada only**; organisations include Israel. Paid Azure subscription required. Issues no EV |
| Sectigo individual / OV | ~$220–280/year | Still sold to a person rather than a company. FIPS token required |
| Sectigo EV | ~$280–300/year | Registered legal entity required |
| DigiCert EV | ~$560–645/year | Same, at roughly twice the price |

**Reputation is now identical across all of them.** This table used to say EV was immediate and
worth the premium for a consumer download with no history. Microsoft removed instant SmartScreen
reputation for EV in 2024, in its Trusted Root Program requirements; reputation accrues per file
hash for OV and EV alike. Nothing here buys a first-day pass, so the cheapest certificate that can
actually be obtained is the right one.

Certificates have been capped at one year since 15 February 2026, so this is a recurring cost.

### Getting one through Azure Artifact Signing

The chosen path, because the publisher is an Israeli עוסק מורשה of more than three years: Israel is
on the organisation list, organisation validation requires three or more years of verifiable tax
history, and the alternative costs roughly twice as much and arrives with a hardware token to keep
safe.

**Two things block the form before the portal is even opened.** Organisation validation demands a
**website belonging to the legal entity** and a **primary email on a domain that entity owns**,
with the secondary email on the same domain. A Gmail address fails. That means buying a domain —
which the launch needs anyway, because `web/privacy.html` must be hosted publicly for Chrome Web
Store approval. One domain closes both, so it is the first thing to buy.

Then, in order:

1. A **paid** Azure subscription. Free, trial and sponsored subscriptions are refused outright.
2. Register the resource provider: `az provider register --namespace "Microsoft.CodeSigning"`.
3. Create an Artifact Signing account, Basic SKU. **Israel Central is not a supported region for
   the service** — use West Europe. The geography that matters is the entity's, not the resource's.
4. Assign yourself the **Artifact Signing Identity Verifier** role, or "New identity" stays greyed
   out with no explanation.
5. Identity validations → Organization → Public. The **Organization Name is the validated legal
   name and appears in the certificate**; for a sole proprietor that is usually the person's own
   name, and neither CN nor O can be customised. The business identifier is the עוסק מורשה number.
   First and last name must match the government ID exactly.
6. Identity verification as the organisation's representative, through AU10TIX and Verified ID in
   Microsoft Authenticator.
7. **Wait 1–20 business days.** It cannot be expedited, and duplicate requests for one entity make
   it worse. Two hard limits: the email verification link **expires after seven days** and a missed
   one means starting over, and requests for further documents allow **three attempts only**.
8. Create a Public Trust certificate profile.

**Signing setup, where the time actually goes:** Windows SDK 10.0.2261.755 or later — older ones
silently do not work — and the **64-bit** `signtool.exe` with the **64-bit**
`Azure.CodeSigning.Dlib.dll`. Mixing architectures crashes with nothing in the console and an entry
only in Event Viewer. The `Artifact Signing Certificate Profile Signer` role goes on the **App
Registration**, not the main account; getting that wrong is the usual cause of a 403.

**Two things expire quietly.** Identity validation, with reminders from 60 days out — miss it and
certificate renewal stops and all signing halts. And the App Registration's client secret, which
fails the build instead. Put both in a calendar.

Once a certificate exists, `installer/AutoLang.iss` signs during the build:

```
iscc /S"signtool=signtool.exe sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f" installer\AutoLang.iss
```

---

## Order of operations

1. **Buy the domain.** It comes first now, not because it is urgent in itself but because two
   later steps are blocked on it: Artifact Signing will not validate an organisation without a
   website and an email on the entity's own domain, and the store will not approve without a
   publicly hosted privacy policy.
2. Start the certificate — everything else can proceed in parallel, and this cannot be hurried
3. Publish the landing page and privacy policy
4. Verify selectors against live WhatsApp
5. Take screenshots from a test account
6. Register the developer account ($5, one-off) and complete identity verification
7. Submit to Chrome, then Edge
8. Sign the installer as soon as the certificate arrives
