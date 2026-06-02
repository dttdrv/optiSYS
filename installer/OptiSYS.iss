; optiSYS Setup — Inno Setup 6 (https://jrsoftware.org/isinfo.php)
;
; Chrome-style minimal installer: run -> UAC (admin) -> installs immediately in a small window
; showing only the app icon + a progress bar. On completion the same window shows a
; "Pin to desktop" checkbox and a centered "Launch optiSYS" button. No welcome page, no feature
; questions, no directory/component selection. All on-brand feature setup happens in the app's
; WinUI first run, not here.
;
; Build the payload, then compile:
;   dotnet publish src\OptiSYS.App -c Release -r win-x64 --self-contained true -o installer\publish\release-win-x64
;   iscc installer\OptiSYS.iss        (run from the project root)

#define AppName "optiSYS"
#define AppPublisher "Deyan Todorov"
#define AppExeName "OptiSYS.exe"
#ifndef AppVersion
  #define AppVersion "0.4.0"
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
; Chrome-style minimal: skip welcome/dir/group/finished, but KEEP the Ready page — it is the
; natural pre-copy gate that carries the "Install" button. We style it down to icon + a single
; Install button (see [Code]); progress then runs; a custom finish state shows pin + auto-launches.
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=no
DisableFinishedPage=yes
; Install per-user into %LOCALAPPDATA% with NO upfront UAC (Chrome-style: it just installs). The
; only elevation is the post-copy provisioning of the silent elevated logon task, which the
; ssPostInstall step requests on its own via ShellExec 'runas' (one UAC, at the end).
PrivilegesRequired=lowest
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
SetupIconFile={#AssetDir}\SetupIcon.ico
; Inno 6 loads PNG natively into WizardSmallBitmapImage; we reparent that to show the app icon
; in the compact window body (see [Code]).
WizardSmallImageFile={#AssetDir}\wizard-small.png
OutputDir={#OutputDir}
OutputBaseFilename=optiSYS-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; The app icon shown in the installer window (extracted from the published assets at runtime).

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
; Desktop shortcut is created on demand from the custom finish checkbox (see [Code]); no Task here.

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "TerminateApp"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\optiSYS"
Type: filesandordirs; Name: "{localappdata}\optiSYS"

[Code]
var
  TitleLabel: TNewStaticText;
  PinCheckBox: TNewCheckBox;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Close any running instance so files aren't locked during an upgrade.
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure InitializeWizard();
begin
  // Compact, chrome-light window: hide the header/bevel so only the icon + page body show. We KEEP
  // Inno's own Next/Install/Cancel buttons (the Ready page's Install button is the explicit gate)
  // rather than hand-rolling one — far more robust than racing the install sequence.
  WizardForm.Caption := '{#AppName} Setup';
  WizardForm.MainPanel.Visible := False;
  WizardForm.Bevel.Visible := False;
  WizardForm.BackButton.Visible := False;

  // App icon centered on the Ready page body.
  WizardForm.WizardSmallBitmapImage.Parent := WizardForm.ReadyPage;
  WizardForm.WizardSmallBitmapImage.Left := (WizardForm.ReadyPage.Width - WizardForm.WizardSmallBitmapImage.Width) div 2;
  WizardForm.WizardSmallBitmapImage.Top := ScaleY(24);
  WizardForm.WizardSmallBitmapImage.Visible := True;

  // Replace the Ready page's verbose memo with a single centered line.
  WizardForm.ReadyMemo.Visible := False;
  TitleLabel := TNewStaticText.Create(WizardForm);
  TitleLabel.Parent := WizardForm.ReadyPage;
  TitleLabel.Caption := 'Ready to install {#AppName}.';
  TitleLabel.Font.Size := 11;
  TitleLabel.AutoSize := True;
  TitleLabel.Top := ScaleY(92);
  TitleLabel.Left := (WizardForm.ReadyPage.Width - TitleLabel.Width) div 2;

  // Pin-to-desktop choice lives on the Ready page; LaunchApp() (on ssDone) reads it. Default on.
  PinCheckBox := TNewCheckBox.Create(WizardForm);
  PinCheckBox.Parent := WizardForm.ReadyPage;
  PinCheckBox.Caption := 'Pin to desktop';
  PinCheckBox.Width := ScaleX(160);
  PinCheckBox.Top := ScaleY(124);
  PinCheckBox.Left := (WizardForm.ReadyPage.Width - PinCheckBox.Width) div 2;
  PinCheckBox.Checked := True;
end;

procedure LaunchApp();
var
  ResultCode: Integer;
begin
  if PinCheckBox <> nil then
    if PinCheckBox.Checked then
      CreateShellLink(
        ExpandConstant('{autodesktop}\{#AppName}.lnk'),
        '', ExpandConstant('{app}\{#AppExeName}'), '',
        ExpandConstant('{app}'), '', 0, SW_SHOWNORMAL);
  ShellExec('open', ExpandConstant('{app}\{#AppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // On the Ready page, relabel the Next button to "Install" (Inno usually does this; force it for
  // the trimmed UI) so the single visible button reads clearly.
  if CurPageID = wpReady then
    WizardForm.NextButton.Caption := 'Install';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Per-user install (no elevation). Elevate ONLY this step (one UAC) to provision the silent
    // elevated logon task: the app's --provision-elevation branch registers the HighestAvailable
    // logon task, flips UseTaskScheduler on, and exits without a window.
    ShellExec('runas', ExpandConstant('{app}\{#AppExeName}'), '--provision-elevation', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurStep = ssDone then
  begin
    // Install finished — launch the app automatically (reliable; not dependent on a custom button).
    LaunchApp();
  end;
end;
