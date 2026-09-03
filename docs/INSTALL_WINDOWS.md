# Installing on Windows

Two pieces have to be installed, and they are separate for a reason: a browser extension cannot
change your keyboard layout — only Windows can do that, and only a native program can ask it to.

1. **The agent** — a 12MB program that changes the layout and remembers your preferences.
2. **The browser extension** — reads the conversation and tells the agent what it sees.

Neither works without the other. The extension will say so plainly if the agent is missing.

## Requirements

- Windows 10 or 11, 64-bit
- Chrome or Edge, recent version
- **Hebrew and English keyboard layouts added in Windows** — see below

No .NET runtime is needed; the agent carries its own.

## Check your keyboard layouts first

The product switches between layouts you already have. If Hebrew is not installed in Windows, there
is nothing to switch to, and the popup will tell you so.

**Settings → Time & language → Language & region.** You need Hebrew and English both listed.

To confirm what the product can actually see:

```powershell
& "$env:LOCALAPPDATA\Programs\AutoLang\AutoLang.exe" --layouts
```

```
0xF03D040D 0002040d Hebrew (Standard)
0x04090409 00000409 US

Hebrew   0xF03D040D 0002040d Hebrew (Standard)
English  0x04090409 00000409 US
```

`NOT INSTALLED` against either means Windows does not have that layout, and no setting in this
product can work around it.

## Install the agent

### From a build

```powershell
.\build.ps1
.\installer\Install.ps1
```

Run it from a PowerShell window you opened yourself. If anything else runs it — an agent, a CI step,
a packaged or sandboxed shell — add `-Verify`:

```powershell
.\installer\Install.ps1 -Verify
```

Those environments can have their registry writes virtualised, so the registration lands where the
browser will never look while every check inside the installer reports success. `-Verify` reads the
result back through a separate process and fails loudly instead. See
[TROUBLESHOOTING.md](TROUBLESHOOTING.md).

### What it does

Everything is per-user. No administrator rights are needed, and nothing is changed for other
accounts on the machine.

| | |
|---|---|
| `%LOCALAPPDATA%\Programs\AutoLang` | The program |
| `HKCU\...\NativeMessagingHosts\com.autolang.bridge` | How Chrome and Edge find it |
| `HKCU\...\CurrentVersion\Run` | Starts it when you sign in |
| `HKCU\...\Uninstall\AutoLang` | So it appears in Apps and Features |

A tray icon appears when it is running. Right-click it for status, an on/off switch, and the data
folder.

## Install the extension

Until the Chrome Web Store listing is live this is a manual step.

1. Open `chrome://extensions` (or `edge://extensions`)
2. Turn on **Developer mode**
3. **Load unpacked**, and choose the `extension\dist` folder
4. Check the ID reads `iblcjhakhfggopgijnankilmifbjbdbp`

Step 4 matters. The agent only accepts that exact extension id, so a different one is refused. If
it differs, the extension was built without its signing key — rebuild with `.\build.ps1`.

## Check it works

Open `https://web.whatsapp.com` and click the extension icon. You should see the current layout,
what it decided for this conversation, and why.

To test the whole chain without a browser:

```bash
node tools\bridge-smoke-test.mjs "%LOCALAPPDATA%\Programs\AutoLang\AutoLang.exe"
```

Four passes means the extension, the native messaging host, the pipe, the agent and the layout
service are all talking to each other.

## Removing it

**Settings → Apps → Auto Language Switcher**, or:

```powershell
& "$env:LOCALAPPDATA\Programs\AutoLang\Uninstall.ps1"
```

Your stored preferences are kept, because uninstalling is often a reinstall. Add `-RemoveData` to
delete those too.

The extension has to be removed separately, from `chrome://extensions`. Nothing outside the browser
can remove it.

## Upgrading

Run `Install.ps1` again. It stops the running agent first — a resident program holds its own
executable open, so this is the normal upgrade path rather than an edge case. Your preferences are
untouched.
