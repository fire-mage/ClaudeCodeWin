; ClaudeCodeWin Inno Setup Script
;
; Local build:
;   ISCC /DMyAppVersion=1.0.0 /DPublishDir=publish build\installer.iss
;
; CI build (GitHub Actions):
;   ISCC /DMyAppVersion=1.0.5 /DPublishDir=..\publish\win-x64 build\installer.iss

#define MyAppName "ClaudeCodeWin"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
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
OutputBaseFilename=ClaudeCodeWin-Setup-{#MyAppVersion}-{#Arch}
SetupIconFile=..\src\ClaudeCodeWin\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Start with Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
