; ═══════════════════════════════════════════════════════════════════════════════════════════════
;  Kacher POS — Windows installer (Inno Setup 6)
;
;  Build:
;      iscc installer\KacherPos.iss            (or: .\build-installer.ps1)
;
;  Expects a published application folder at ..\dist\KacherPos (produced by build-exe.ps1 or the
;  GitHub workflow). The result is a single setup executable a customer can double-click.
;
;  What it does beyond copying files:
;    * per-user install by default (no admin prompt, works on locked-down tills)
;    * Start-menu + optional desktop shortcut, correct AppUserModelID so the taskbar icon groups
;    * file-association-free, but registers an uninstall entry with the product name and version
;    * keeps %LOCALAPPDATA%\Kacher (database, backups, reports) intact on uninstall or upgrade
; ═══════════════════════════════════════════════════════════════════════════════════════════════

#define AppName        "Kacher POS"
#define AppShortName   "KacherPOS"
#define AppPublisher   "Kacher Retail Systems"
#define AppUrl         "https://kacher.local"
#define AppExe         "Kacher.Pos.exe"
#define AppId          "{{8B2C41D6-7F1A-4C8E-9F52-4E1B6D3A9C77}"
#define AppVersion     GetEnv("KACHER_VERSION")

#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} setup
VersionInfoProductName={#AppName}

; Per-user by default: a POS terminal often has no local admin during deployment.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 64-bit only: the app is published as win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename={#AppShortName}-{#AppVersion}-setup
SetupIconFile=..\src\Kacher.Pos\Assets\kacher.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
CloseApplications=yes
RestartApplications=no
LicenseFile=..\docs\EULA.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Start {#AppName} automatically when Windows starts (recommended for a till)"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; The whole publish folder, recursively. Excludes nothing — a self-contained build needs every file.
Source: "..\dist\KacherPos\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppPublisher}.{#AppShortName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "{#AppPublisher}.{#AppShortName}"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only generated files inside the install folder; the database lives in %LOCALAPPDATA% and is kept
; on purpose so a reinstall never loses sales history.
Type: filesandordirs; Name: "{app}\logs"

[Code]
{ Refuse to install over a running instance: replacing a loaded exe fails halfway and leaves a broken
  shortcut. The app holds a named mutex for the lifetime of the process, so the check is exact. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('{#AppShortName}-running') then
    Result := 'Kacher POS is running. Close the till, then run setup again.';
end;

[Registry]
; AppUserModelID for the shortcut so Windows groups it with the running window.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppPublisher}.{#AppShortName}"; \
    ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
