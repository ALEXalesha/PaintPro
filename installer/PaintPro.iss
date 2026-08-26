; Inno Setup script for Paint Pro (WPF).
; Packages the self-contained single-file dist\PaintPro.exe into an installer .exe
; with Start Menu + optional desktop shortcuts and an uninstaller.
; Build:  ISCC.exe installer\PaintPro.iss

#define MyAppName "Paint Pro"
#define MyAppVersion "1.21.0"
#define MyAppPublisher "Paint Pro"
#define MyAppExeName "PaintPro.exe"

[Setup]
AppId={{9F3C2E7A-5B1D-4C8E-9A2F-1E7D6B4A3C50}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Paint Pro
DefaultGroupName=Paint Pro
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=PaintPro-Setup-{#MyAppVersion}
SetupIconFile=..\src\PaintPro.Wpf\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
