; ClaudeCodeWin Inno Setup Script
; Prerequisites:
;   1. Run build\publish.ps1 first
;   2. Install Inno Setup 6: https://jrsoftware.org/isinfo.php
;   3. Compile this script: ISCC build\installer.iss
;      or open in Inno Setup GUI and press Ctrl+F9

#define MyAppName "ClaudeCodeWin"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "main.fish"
#define MyAppExeName "ClaudeCodeWin.exe"
#define MyAppURL "https://main.fish"

[Setup]
AppId={{B8F3A2D1-7C4E-4F9B-A6D8-3E5F1C2B9A70}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=ClaudeCodeWin-Setup-{#MyAppVersion}
SetupIconFile=..\src\ClaudeCodeWin\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main executable (single-file publish output)
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Any additional files from publish (WPF may produce a few runtime DLLs)
Source: "publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "publish\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
