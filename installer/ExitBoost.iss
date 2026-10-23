; Установщик ExitBoost (Inno Setup 6).
;
; Сборка:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\ExitBoost.iss
;   ... /DAppVersion=1.0.1 /DSourceDir=publish\ExitBoost /O"publish\release"
;
; Устанавливается как обычная программа: «Программы и компоненты», ярлыки,
; удаление через uninstaller. По умолчанию — в Program Files (нужны права
; администратора, они всё равно нужны самому приложению); при желании можно
; выбрать установку «только для меня».

#define MyAppName "ExitBoost"
#define MyAppExeName "ExitBoost.exe"
#define MyAppPublisher "ExitBoost"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\ExitBoost"
#endif

[Setup]
AppId={{7C1F4B9A-2E64-4D51-9B3E-5C7D8A1F2B60}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\publish\release
OutputBaseFilename=ExitBoost-{#AppVersion}-setup
SetupIconFile=..\src\ExitBoost.App\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser
