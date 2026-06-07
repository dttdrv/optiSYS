; optiSYS Setup — Inno Setup 6 (https://jrsoftware.org/isinfo.php)
;
; Compact auto-installer: run -> UAC (admin, once) -> a small window shows the app icon + a progress
; bar and installs immediately with NO clicks, then launches the app. How the no-click install works
; (verified against a throwaway lowest-privilege test harness): every wizard page is disabled, but Inno still surfaces
; the Ready page when no earlier page was shown. Calling NextButton.OnClick directly from
; CurPageChanged is ignored (the wizard is mid-transition), so we defer the click one message-loop
; tick with a Windows timer; the Next button is moved off-screen rather than hidden, because a hidden
; Next button ignores OnClick. All on-brand feature setup happens in the app's WinUI first run.
;
; Build the payload, then compile:
;   dotnet publish src\OptiSYS.App -c Release -r win-x64 --self-contained true -o installer\publish\release-win-x64
;   iscc installer\OptiSYS.iss        (run from the project root)

#define AppName "optiSYS"
#define AppPublisher "Deyan Todorov"
#define AppExeName "OptiSYS.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0-alpha.1"
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
; No pages: the wizard is driven straight to install (see [Code]); a compact custom window shows
; only the icon + progress.
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
; optiSYS runs ELEVATED via its logon task, so its files (clrjit.dll, etc.) are locked by an
; elevated process; a per-user installer cannot replace them on upgrade (DeleteFile Access denied,
; code 5). Run the installer elevated (one UAC at start) so it can terminate the elevated instance,
; replace files, and provision the logon task in a single elevation. Still a per-user install into
; %LOCALAPPDATA%, and it still auto-runs (all pages disabled) once elevation is granted.
; INVARIANT: assumes the logged-in user is a local admin (same-user UAC consent) — the single-user
; target. A standard user elevating with a *different* admin's credentials would resolve {localappdata}
; and the logon-task SID to that admin's profile, misrouting the install.
PrivilegesRequired=admin
; Always write a setup log to %TEMP% ("Setup Log YYYY-MM-DD #NNN.txt") so any install issue leaves
; hard evidence to diagnose from.
SetupLogging=yes
; We terminate the running (elevated) app ourselves in InitializeSetup, so keep Inno's restart
; manager out of it — CloseApplications=yes would otherwise surface a "close applications" page that
; would stall the unattended install.
CloseApplications=no
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
SetupIconFile={#AssetDir}\SetupIcon.ico
WizardSmallImageFile={#AssetDir}\wizard-small.png
OutputDir={#OutputDir}
OutputBaseFilename=optiSYS-{#AppVersion}-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "TerminateApp"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\optiSYS"
Type: filesandordirs; Name: "{localappdata}\optiSYS"

[Code]
var
  gTimer: LongWord;
  gArmed: Boolean;
  TitleLabel: TNewStaticText;

function SetTimer(hWnd, nIDEvent, uElapse, lpTimerFunc: LongWord): LongWord; external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd, uIDEvent: LongWord): LongWord; external 'KillTimer@user32.dll stdcall';

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // The installer is elevated, so end the elevated logon task and force-kill any running instance
  // BEFORE the file copy — otherwise the elevated process keeps clrjit.dll (etc.) open and the
  // replace fails with Access denied (code 5). An elevated Exec can terminate the elevated process.
  Exec('schtasks.exe', '/End /TN "{#AppName}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

{ Deferred Next click — runs on the next message-loop tick, after CurPageChanged returns and the
  page has settled, so the click actually advances the wizard. }
procedure ClickNextTimer(H, Msg, IDEvent, Time: LongWord);
begin
  KillTimer(0, gTimer);
  gTimer := 0;
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure InitializeWizard();
begin
  // Compact, chrome-light window: hide the standard header, bevels and Back/Cancel buttons so only
  // the app icon + progress show. The Next button is NOT hidden (a hidden Next button ignores
  // OnClick); it is moved off the visible client area so the auto-advance still works.
  WizardForm.Caption := '{#AppName} Setup';
  WizardForm.ClientWidth := ScaleX(360);
  WizardForm.ClientHeight := ScaleY(210);
  WizardForm.Position := poScreenCenter;

  WizardForm.MainPanel.Visible := False;
  WizardForm.Bevel.Visible := False;
  WizardForm.BackButton.Visible := False;
  WizardForm.CancelButton.Visible := False;
  WizardForm.OuterNotebook.Visible := False;
  WizardForm.NextButton.Top := ScaleY(600);

  // App icon: reuse Inno's natively-loaded small bitmap (from WizardSmallImageFile), reparented and
  // centered near the top of the compact window.
  WizardForm.WizardSmallBitmapImage.Parent := WizardForm;
  // Blend the icon's transparent margin into the form: Inno's TBitmapImage composites alpha over its
  // BackColor, which (defaulting to white) otherwise shows as a white box around the icon.
  WizardForm.WizardSmallBitmapImage.BackColor := WizardForm.Color;
  WizardForm.WizardSmallBitmapImage.Left := (WizardForm.ClientWidth - WizardForm.WizardSmallBitmapImage.Width) div 2;
  WizardForm.WizardSmallBitmapImage.Top := ScaleY(22);
  WizardForm.WizardSmallBitmapImage.Visible := True;

  TitleLabel := TNewStaticText.Create(WizardForm);
  TitleLabel.Parent := WizardForm;
  TitleLabel.Caption := 'Installing {#AppName}…';
  TitleLabel.Font.Size := 11;
  TitleLabel.AutoSize := True;
  TitleLabel.Top := ScaleY(126);
  TitleLabel.Left := (WizardForm.ClientWidth - TitleLabel.Width) div 2;

  WizardForm.ProgressGauge.Parent := WizardForm;
  WizardForm.ProgressGauge.Width := ScaleX(280);
  WizardForm.ProgressGauge.Left := (WizardForm.ClientWidth - WizardForm.ProgressGauge.Width) div 2;
  WizardForm.ProgressGauge.Top := ScaleY(158);
  WizardForm.ProgressGauge.Visible := True;
end;

{ All wizard pages are disabled, but Inno still surfaces the Ready page (DisableReadyPage is ignored
  when no earlier page was shown). Calling NextButton.OnClick directly here is ignored (mid-
  transition), so defer it one message-loop tick via a timer. This auto-starts the install with no
  click (verified end-to-end against a lowest-privilege test harness). gArmed latches so the click
  that drives wpReady -> install can never re-enter and schedule a second click on a later page. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (not gArmed) and ((CurPageID = wpReady) or (CurPageID = wpPreparing)) then
  begin
    gArmed := True;
    gTimer := SetTimer(0, 0, 50, CreateCallback(@ClickNextTimer));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
    // Already elevated: provision the silent elevated logon task directly (no second UAC). The app's
    // --provision-elevation branch registers the HighestAvailable logon task, flips UseTaskScheduler
    // on, and exits without a window. From the next logon the app launches elevated silently.
    ShellExec('open', ExpandConstant('{app}\{#AppExeName}'), '--provision-elevation', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if CurStep = ssDone then
    // Launch the app once install completes (Finished page disabled; the compact window closes and
    // the app opens). The desktop + start-menu shortcuts are created by the [Icons] section.
    ShellExec('open', ExpandConstant('{app}\{#AppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
