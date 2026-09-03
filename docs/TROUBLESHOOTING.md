# Troubleshooting

The popup names the reason for whatever it is doing. Start there — most of what follows is the
product working correctly and saying so.

## Nothing switches at all

### "The desktop agent is not running"

The extension cannot reach the agent. In order of likelihood:

```powershell
# 1. Is it running?
Get-Process AutoLang

# 2. Start it.
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe"

# 3. Can Chrome find it?
Get-ItemProperty "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.autolang.bridge"

# 4. Does the file that points to actually exist?
Get-Content "$env:LOCALAPPDATA\Programs\AutoLang\com.autolang.bridge.json"
```

Three things have to agree — the registry value, the manifest file, and the executable path inside
it. When they disagree, Chrome reports a disconnect that names none of them. Re-running
`Install.ps1` rewrites all three from one source.

### "Specified native messaging host not found", while the registry looks correct

**Check this one first if the install was run by anything other than you, at a PowerShell window you
opened yourself.** An agent, a CI step, a packaged (MSIX) host or a sandboxed shell can have its
registry writes virtualised: the value goes into a private view, the writing process reads it
straight back and sees it there, and every other process on the machine — the browser included —
finds nothing at that path.

Nothing observable from inside the installing shell tells the two apart. `reg query` prints the key.
`Get-ItemProperty` returns the value. Reading through `HKEY_USERS\<SID>` instead of `HKCU` returns
the same virtualised view, and `GetCurrentPackageFullName` reports no package identity. The only
question that separates them is whether a *different* process can see the value:

```powershell
.\tools\verify-registration.ps1
```

It reads the registration through a process started outside this one and says plainly whether a
browser would find the host. A clean install performed this way is worth the habit:

```powershell
.\installer\Install.ps1 -Verify
```

If it reports the registration missing, re-run the installer from an ordinary PowerShell window
opened from the Start menu. Nothing is wrong with the product; the registration simply never reached
the real hive.

This cost a full debugging session once. The manifest was valid, byte-order-mark free and pointed at
a file that existed; the registry key was present with the right default value; no enterprise policy
was set; the host name matched in the bundle, the manifest and the key; fourteen browser variants
were registered. Chrome still said the host was missing, and it was right.

---

The next cause is a byte order mark in the manifest. Chrome's JSON parser rejects a file that begins with one
and then reports the host as missing — an error that sends you looking at the registry while the
fault is three bytes at the front of a file.

```powershell
$m = "$env:LOCALAPPDATA\Programs\AutoLang\com.autolang.bridge.json"
$b = [System.IO.File]::ReadAllBytes($m)
"has BOM: " + ($b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
```

Re-running `Install.ps1` rewrites it correctly, and both installer scripts now read the file back
and refuse to finish if a BOM appears. Windows PowerShell 5.1's `Out-File -Encoding utf8` writes
one, which is how this happened in the first place.

**The next most common cause is the extension id.** The agent's allowlist names one id exactly. Open
`chrome://extensions` and check yours reads `iblcjhakhfggopgijnankilmifbjbdbp`. If it does not, the
extension was built without its signing key and every connection is refused by design.

### "Hebrew is not installed in Windows"

Exactly what it says. **Settings → Time & language → Language & region**, add Hebrew. Confirm with:

```powershell
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --layouts
```

### "WhatsApp layout not recognized"

WhatsApp changed its page structure and the adapter stopped making decisions rather than guess.
This is the designed failure: a wrong switch mid-sentence is worse than no switch.

Run [`tools/whatsapp-selector-probe.js`](../tools/whatsapp-selector-probe.js) in the DevTools
console on an open conversation. It reports which selectors still match. The output contains only
tier numbers and letter counts — phone numbers are reduced to their shape — so it is safe to share
in a bug report.

## It switches, but not when I expect

Every one of these is a rule doing its job. The popup says which.

| Popup says | Meaning |
|---|---|
| You are in the middle of typing | The composer is not empty. Switching mid-sentence would corrupt what you are writing, so it waits. |
| This conversation is too mixed to call | Hebrew and English are too close to choose between. It changes nothing rather than guess. |
| Not enough of your own messages yet | Fewer than five weighted letters of your own writing. It reads what *you* type, not what you receive. |
| The browser is not in front | Only the foreground window is touched. A background tab must never change the keyboard of whatever you are actually typing in. |
| You changed it yourself just now | You overrode it, so it backs off for five minutes rather than argue. |
| Just switched, waiting a moment | At most one switch per 750ms, which is what stops a flip-flop loop. |

### It picks English in a Hebrew conversation

If you write Hebrew in Latin letters — "ma nishma achi" — English is correct. The question is which
keys you are about to press, not which language you are speaking.

If that is not it, the other person's language is probably outvoting nothing: the product weighs
**your** messages, so a conversation where you have written little has little to go on. Pin it with
**עברית** in the popup, and the pin wins over everything.

### It switches to a language I did not want

Open the popup and pin the conversation. A pin beats analysis, memory and defaults.

If it happens across many conversations, raise **Confidence needed to switch** in Settings. Higher
means fewer switches and fewer wrong ones.

## It worked, then stopped

Chrome suspends extension service workers when idle. The connection reopens on the next thing that
happens in the conversation. If it does not, reload the WhatsApp tab.

Confirm the whole chain independently of the browser:

```bash
node tools\bridge-smoke-test.mjs "%LOCALAPPDATA%\Programs\AutoLang\AutoLang.exe"
```

Four passes means everything except the browser is fine.

## Two keyboards fighting

Only one agent should run per session. It holds a single-instance lock, but if you started a build
by hand as well:

```powershell
Get-Process AutoLang | Stop-Process -Force
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe"
```

## A third language

Windows switching to something other than Hebrew or English is not this product: it selects a
layout directly and never cycles. `Alt+Shift` and `Win+Space` still cycle through everything you
have installed, as always.

## Detailed logs

```powershell
Get-Process AutoLang | Stop-Process -Force
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --verbose
Get-Content "$env:LOCALAPPDATA\AutoLang\agent.log" -Wait
```

Each line is one decision: what was chosen, from which source, at what confidence, and whether
Windows accepted it. There is no message text in the log, which also means it cannot tell you what
a conversation said — only what was concluded.

## Starting over

```powershell
# Forget every conversation preference, keep the product installed:
#   tray icon -> Clear stored preferences

# Or remove everything:
& "$env:LOCALAPPDATA\Programs\AutoLang\Uninstall.ps1" -RemoveData
```

## Reporting a problem

Useful to include, and all safe to share:

- `AutoLang.exe --layouts`
- The output of the selector probe
- What the popup said
- `agent.log` from a `--verbose` run

None of these contain message text, contact names or phone numbers.
