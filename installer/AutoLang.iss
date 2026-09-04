; Inno Setup script for Auto Language Switcher.
;
; This is the distributable path: a signed .exe that a user can download and double-click.
; Install.ps1 does the same work and is what runs today, but "download this .ps1 and run it" is
; not something anyone should be asked to do, and an unsigned installer trips SmartScreen anyway.
;
; Build:
;   iscc installer\AutoLang.iss
;
; Requires Inno Setup 6 (https://jrsoftware.org/isdl.php). It is not committed and not fetched
; automatically: a build script that silently downloads an installer compiler is worse than one
; that tells you what is missing.
;
; Signing, once a certificate exists:
;   iscc /S"signtool=signtool.exe sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 $f" installer\AutoLang.iss

#define AppName        "Auto Language Switcher"
#define AppId          "AutoLang"
#define AppVersion     "0.1.0"
#define AppPublisher   "Auto Language Switcher"
#define ExtensionId    "iblcjhakhfggopgijnankilmifbjbdbp"
#define HostName       "com.autolang.bridge"
#define SourceDir      "..\dist\agent"

[Setup]
AppId={{8F2A9C41-6E3D-4B7A-9C15-2D8E4F1A7B03}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppId}
DefaultGroupName={#AppName}
OutputDir=..\dist
OutputBaseFilename=AutoLangSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
DisableDirPage=yes

; Per-user throughout. Everything this product touches - the session's keyboard layout, the
; browser's native messaging keys, the conversation preferences - is already per-user, so asking
; for administrator rights would be asking for power it has no use for.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

UninstallDisplayIcon={app}\AutoLang.exe
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "hebrew";  MessagesFile: "compiler:Languages\Hebrew.isl"

[Files]
; The whole publish folder: the Agent is not a single-file bundle, because Defender quarantines
; that shape. Its runtime DLLs sit beside AutoLang.exe. See AutoLang.Agent.csproj.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The extension is shipped alongside so it can be loaded unpacked until the store listing is live.
Source: "..\extension\dist\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup"

[Registry]
; The native messaging manifest has to name an absolute path, so it is written at install time
; rather than shipped. All three - the two registry values and the file - must agree, or the
; connection fails with an error that names none of them.
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\{#HostName}"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#HostName}.json"; Flags: uninsdeletekey

Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\{#HostName}"; \
    ValueType: string; ValueName: ""; ValueData: "{app}\{#HostName}.json"; Flags: uninsdeletekey

[Icons]
; Autostart is a shortcut in the Startup folder, not a value under HKCU\...\Run.
;
; Measured on 2026-09-05: Defender quarantined the executable as Behavior:Win32/Persistence.A!ml
; and named the Run value among the resources. An unsigned binary written into Run is the shape
; that model looks for, and it deletes rather than warns. See the longer note in Install.ps1;
; the two installers must not disagree about this.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\AutoLang.exe"; Tasks: autostart

[Run]
Filename: "{app}\AutoLang.exe"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\{#HostName}.json"

; Deliberately NOT deleting {localappdata}\AutoLang. Uninstalling is often a reinstall, and
; discarding which language someone uses in each of their conversations is not a decision an
; uninstaller should make for them.

[Code]
procedure WriteHostManifest();
var
  Json: string;
begin
  Json :=
    '{' + #13#10 +
    '  "name": "{#HostName}",' + #13#10 +
    '  "description": "{#AppName} native bridge",' + #13#10 +
    '  "path": "' + StringChangeEx2(ExpandConstant('{app}\AutoLang.exe'), '\', '\\', True) + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_origins": [ "chrome-extension://{#ExtensionId}/" ]' + #13#10 +
    '}';

  SaveStringToFile(ExpandConstant('{app}\{#HostName}.json'), Json, False);
end;

function StringChangeEx2(const Value, FromStr, ToStr: string; SupportDBCS: Boolean): string;
begin
  Result := Value;
  StringChangeEx(Result, FromStr, ToStr, SupportDBCS);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteHostManifest();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // The Agent is resident and holds its own executable open; without this the uninstaller
    // leaves the exe behind and reports success.
    Exec('taskkill.exe', '/F /IM AutoLang.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
