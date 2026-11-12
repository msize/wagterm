; wagterm installer (Inno Setup 6) — per-user, no admin.
; Build via installer\build.ps1 (publishes to stage\ then runs ISCC on this file).

#define AppName    "WagTerm"
#define AppVersion "0.20.13"
#define AppExe     "wagterm.exe"
#define AppPublisher "msize"
; The main app defaults new sessions to the Rust pty-host as a first-run seed only (it never
; overrides an existing config). Lite ships from its own installer (wagterm-lite.iss).
#define RustHostArg "--default-session-host server-rust"

[Setup]
AppId={{B892334A-3C87-4909-A3B9-0D8181DA1B81}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\wagterm
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\Wagterm.Win32\assets\wagterm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=wagterm-setup-{#AppVersion}

; Minimal, non-invasive setup (agterm-style): only copy files + create shortcuts. Integrations
; (wagtermctl on PATH, agent hooks, agent skill, shell integration) are OPT-IN from inside the
; app — action palette (Ctrl+Shift+P) -> the "Install ..." entries — so setup never edits PATH or
; writes to the user's profile/config behind their back.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

; 0.15.0–0.16.0 bundled the lite terminal inside THIS setup; it now ships from its own installer
; (agwinterm-lite-setup-<ver>.exe). This upstream cleanup is not needed in the isolated wagterm setup.

[Icons]
; Main terminal — its shortcut seeds server-rust on a first run (harmless once configured).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; IconFilename: "{app}\assets\wagterm.ico"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; IconFilename: "{app}\assets\wagterm.ico"; Tasks: desktopicon

[Run]
; Launch-on-finish checkbox (interactive installs only) — passes the seed so the very first run
; (typically this launch) defaults the main app to the Rust pty-host.
Filename: "{app}\{#AppExe}"; Parameters: "{#RustHostArg}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
