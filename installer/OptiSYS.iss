; optiSYS Setup — Inno Setup 6 (https://jrsoftware.org/isinfo.php)
;
; Build the payload, then compile:
;   dotnet publish src\OptiSYS.App -c Release -r win-x64 --self-contained true -o installer\publish\release-win-x64
;   iscc installer\OptiSYS.iss        (run from the project root)

#define AppName "optiSYS"
#define AppPublisher "Deyan Todorov"
#define AppExeName "OptiSYS.exe"
#ifndef AppVersion
  #define AppVersion "0.2.1"
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
UninstallDisplayName={#AppName} — System Optimizer
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
; Install unprivileged into %LOCALAPPDATA%. The only elevation is the optional, conditional
; deep-optimization step below (one UAC), so the install itself is frictionless.
PrivilegesRequired=lowest
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
; Solid LZMA2 keeps the distributed setup.exe small even though the self-contained WinUI
; payload is large on disk.
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
SetupIconFile={#AssetDir}\SetupIcon.ico
WizardImageFile={#AssetDir}\wizard-main.png
WizardSmallImageFile={#AssetDir}\wizard-small.png
OutputDir={#OutputDir}
OutputBaseFilename=optiSYS-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Default-ON: enables the admin-only tier (background service tune-up). Ticking it triggers a
; single UAC after copy, which provisions the silent elevated logon task — no prompts thereafter.
Name: "deepoptimize"; Description: "Enable deep system optimization (recommended — asks for administrator once)"; GroupDescription: "Optimization:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "TerminateApp"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
; Roaming settings + Local logs/snapshots. ({userappdata}=Roaming, {localappdata}=Local; the
; install dir {localappdata}\Programs\optiSYS is removed automatically as {app} — distinct path.)
Type: filesandordirs; Name: "{userappdata}\optiSYS"
Type: filesandordirs; Name: "{localappdata}\optiSYS"

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Close any running instance so files aren't locked during an upgrade.
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('deepoptimize') then
    begin
      // Elevate ONLY this step (the one-time UAC). The app's --provision-elevation branch
      // registers the HighestAvailable logon task and flips UseTaskScheduler on, then exits
      // without a window. From the next logon the app launches elevated silently.
      ShellExec('runas', ExpandConstant('{app}\{#AppExeName}'), '--provision-elevation', '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
