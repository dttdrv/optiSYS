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
  #define AppVersion "0.6.0"
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
; Chrome-style: no pages at all. Jump straight to installing; we drive a custom minimal finish.
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
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
  LaunchButton: TNewButton;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Close any running instance so files aren't locked during an upgrade.
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure DoLaunch();
var
  ResultCode: Integer;
begin
  // Create the desktop shortcut if requested, then launch the app.
  if PinCheckBox.Checked then
    CreateShellLink(
      ExpandConstant('{autodesktop}\{#AppName}.lnk'),
      '', ExpandConstant('{app}\{#AppExeName}'), '',
      ExpandConstant('{app}'), '', 0, SW_SHOWNORMAL);

  ShellExec('open', ExpandConstant('{app}\{#AppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

procedure LaunchButtonClick(Sender: TObject);
begin
  DoLaunch();
  WizardForm.Close;
end;

procedure InitializeWizard();
begin
  // Shrink the wizard to a compact, chrome-light window: hide the standard header, bevels and
  // navigation buttons so only our icon + progress (and later the finish controls) are visible.
  WizardForm.Caption := '{#AppName} Setup';
  WizardForm.ClientWidth := ScaleX(380);
  WizardForm.ClientHeight := ScaleY(220);
  WizardForm.Position := poScreenCenter;

  WizardForm.MainPanel.Visible := False;
  WizardForm.Bevel.Visible := False;
  WizardForm.BackButton.Visible := False;
  WizardForm.NextButton.Visible := False;
  WizardForm.CancelButton.Visible := False;
  WizardForm.OuterNotebook.Visible := False;

  // App icon: reuse Inno's natively-loaded small bitmap (from WizardSmallImageFile), reparented
  // and centered near the top of the compact window.
  WizardForm.WizardSmallBitmapImage.Parent := WizardForm;
  WizardForm.WizardSmallBitmapImage.Left := (WizardForm.ClientWidth - WizardForm.WizardSmallBitmapImage.Width) div 2;
  WizardForm.WizardSmallBitmapImage.Top := ScaleY(24);
  WizardForm.WizardSmallBitmapImage.Visible := True;

  TitleLabel := TNewStaticText.Create(WizardForm);
  TitleLabel.Parent := WizardForm;
  TitleLabel.Caption := 'Installing {#AppName}…';
  TitleLabel.Font.Size := 11;
  TitleLabel.AutoSize := True;
  TitleLabel.Top := ScaleY(86);
  TitleLabel.Left := (WizardForm.ClientWidth - TitleLabel.Width) div 2;

  // Reparent the progress bar onto the form, centered.
  WizardForm.ProgressGauge.Parent := WizardForm;
  WizardForm.ProgressGauge.Width := ScaleX(300);
  WizardForm.ProgressGauge.Left := (WizardForm.ClientWidth - WizardForm.ProgressGauge.Width) div 2;
  WizardForm.ProgressGauge.Top := ScaleY(120);
  WizardForm.ProgressGauge.Visible := True;

  // Finish controls — hidden until install completes.
  PinCheckBox := TNewCheckBox.Create(WizardForm);
  PinCheckBox.Parent := WizardForm;
  PinCheckBox.Caption := 'Pin to desktop';
  PinCheckBox.Width := ScaleX(160);
  PinCheckBox.Top := ScaleY(118);
  PinCheckBox.Left := (WizardForm.ClientWidth - PinCheckBox.Width) div 2;
  PinCheckBox.Visible := False;

  LaunchButton := TNewButton.Create(WizardForm);
  LaunchButton.Parent := WizardForm;
  LaunchButton.Caption := 'Launch {#AppName}';
  LaunchButton.Width := ScaleX(150);
  LaunchButton.Height := ScaleY(30);
  LaunchButton.Top := ScaleY(150);
  LaunchButton.Left := (WizardForm.ClientWidth - LaunchButton.Width) div 2;
  LaunchButton.Visible := False;
  LaunchButton.OnClick := @LaunchButtonClick;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // The install itself is per-user (no elevation). Elevate ONLY this step (one UAC) to provision
    // the silent elevated logon task: the app's --provision-elevation branch registers the
    // HighestAvailable logon task, flips UseTaskScheduler on, and exits without a window. From the
    // next logon the app launches elevated silently.
    ShellExec('runas', ExpandConstant('{app}\{#AppExeName}'), '--provision-elevation', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;

  if CurStep = ssDone then
  begin
    // Swap progress for the finish controls in the same window.
    if TitleLabel <> nil then TitleLabel.Caption := '{#AppName} is ready.';
    if WizardForm.ProgressGauge <> nil then WizardForm.ProgressGauge.Visible := False;
    if PinCheckBox <> nil then PinCheckBox.Visible := True;
    if LaunchButton <> nil then LaunchButton.Visible := True;

    // Auto-launch so the app always opens after install (the Launch button stays as a manual
    // backup). Default the desktop shortcut on, since the auto-launch fires before the user
    // toggles the checkbox.
    if PinCheckBox <> nil then PinCheckBox.Checked := True;
    DoLaunch();
  end;
end;
