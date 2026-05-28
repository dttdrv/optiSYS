#define AppName "optiSYS"
#define AppPublisher "Deyan Todorov"
#define AppExeName "OptiSYS.exe"
#ifndef AppVersion
  #define AppVersion "0.0.3"
#endif
#ifndef PublishDir
  #define PublishDir "..\installer\publish\release-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\installer\dist"
#endif
#define AssetDir "assets\generated"

[Setup]
AppId={{E4F388D8-1D5A-4D66-9E33-77F90B8B2438}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#AssetDir}\SetupIcon.ico
WizardImageFile={#AssetDir}\wizard-main.png
WizardSmallImageFile={#AssetDir}\wizard-small.png
OutputDir={#OutputDir}
OutputBaseFilename=optiSYS-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
