; Voice IME — Inno Setup installer
;
; Build (after `dotnet publish ... -o publish` at the repo root):
;   ISCC.exe installer\VoiceIme.iss
; Output: installer\Output\VoiceIme-Setup-<version>.exe
;
; Design decisions:
; * Per-user install (PrivilegesRequired=lowest): a tray dictation app must
;   never demand admin rights; installs under %LocalAppData%\Programs.
; * AppMutex matches SingleInstance.Acquire's mutex name, so Setup politely
;   asks the user to close a running Voice IME before install/uninstall.
; * No installer autostart task: the app itself owns the Startup shortcut
;   (Advanced → Autostart reconciles it on every launch), so an installer
;   shortcut would be removed at first run. Users enable autostart in-app.
; * Settings/history under %AppData%\VoiceIme are the user's data — the
;   uninstaller leaves them alone.

#define MyAppName "Voice IME"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Voice IME contributors"
#define MyAppURL "https://github.com/arvindg4u/voice-ime-windows"
#define MyAppExeName "VoiceIme.exe"

[Setup]
AppId={{800E5E97-F1A9-4C87-A88A-32E19778E2E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=VoiceIme-Setup-{#MyAppVersion}
SetupIconFile=..\src\VoiceIme\Assets\voiceime.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 8 self-contained WPF needs Windows 10 1607+; 10.0.17763 is a safe floor.
MinVersion=10.0.17763
; Matches SingleInstance.Acquire("VoiceIme.SingleInstance") — Setup prompts
; the user to quit a running instance instead of failing mid-copy.
AppMutex=VoiceIme.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Single-file self-contained publish output (repo root /publish).
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the app-managed Startup shortcut so autostart does not dangle after
; uninstall (the app would otherwise be gone but still "registered" to run).
Type: files; Name: "{userstartup}\Voice IME.lnk"
