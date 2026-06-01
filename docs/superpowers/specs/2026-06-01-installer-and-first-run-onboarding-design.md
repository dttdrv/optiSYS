# Chrome-style installer + WinUI stepped first-run onboarding

Date: 2026-06-01
Status: Approved (design), pending spec review → implementation
Build order: installer first, then app onboarding.

## Goal

Two pieces that together give OptiSYS a premium, consistent setup experience:

1. **Installer** — a minimal, Chrome-style Inno Setup installer: no feature questions, no
   wizard pages; it just installs, then offers "pin to desktop" + "launch".
2. **App first-run onboarding** — the actual feature setup (Wi-Fi / Battery / Memory+level),
   rendered in WinUI so it is automatically on-brand (Fluent design, theming, animations).

The key principle (resolved during brainstorming): Inno cannot render WinUI design/animations, so
ALL on-brand setup lives in the app's first run; the installer is a dumb, near-invisible
file-copier. This mirrors Chrome / VS Code / Discord.

## Part 1 — Minimal installer (Inno Setup)

Behavior: run `setup.exe` → immediate UAC (admin) → installation starts instantly → a small fixed
window shows the **app icon + a progress bar** and nothing else. On completion, the same window
replaces the progress bar with a **"Pin to desktop"** checkbox and a centered **"Launch optiSYS"**
button below it. Clicking Launch starts the app and closes the installer.

Changes to `installer/OptiSYS.iss`:
- Suppress all standard pages: keep `DisableDirPage`, `DisableProgramGroupPage`, `DisableReadyPage`;
  add `DisableWelcomePage=yes` and `DisableFinishedPage=yes`. No component/task selection page.
- **Elevation reconciliation (proposed):** change `PrivilegesRequired=lowest` →
  `PrivilegesRequired=admin` so the installer asks for admin once up front (Chrome model). Because
  the app is meant to run elevated at all times, the installer provisions the elevated logon task
  directly in `ssPostInstall` (the `--provision-elevation` ShellExec, now unconditional rather than
  gated on a "deep optimize" checkbox). The `deepoptimize` Task is removed — that question no longer
  exists in the installer.
  - Install location: with admin we may keep `{localappdata}\Programs\optiSYS` (per-user) or move to
    `{autopf}` (Program Files). DECISION for review: keep `{localappdata}\Programs\optiSYS` to avoid
    changing the app's existing path assumptions and uninstall cleanup; admin is used only for the
    logon-task provisioning, not the file location. (Flag if Program Files is preferred.)
- **Custom finish UI:** the "pin to desktop" checkbox + "Launch" button is a custom `[Code]` page
  shown at the end (Inno's finished page is disabled; we draw a minimal custom page with the icon,
  the progress, and on completion the checkbox + launch button). Desktop shortcut created when the
  checkbox is ticked (replaces the old `desktopicon` task default-unchecked behavior).
- Keep: `taskkill` on upgrade/uninstall, the `[UninstallRun]`/`[UninstallDelete]` cleanup,
  LZMA2 compression, icon/version defines.

What is REMOVED from the installer: the "deep optimization" task/checkbox, the welcome image page,
the finished page's standard layout, any feature questions.

## Part 2 — WinUI stepped first-run onboarding (in-shell)

Trigger: in `MainWindow`, on launch, if `!_settings.HasCompletedOnboarding`, show a full-window
**onboarding layer** — a `Grid` toggled via `Visibility`, the SAME pattern the existing
Dashboard/Settings tabs already use (which run crash-free). Explicitly NOT a `ContentDialog`,
`NavigationView`, or new `Window` (the unconfirmed Microsoft.UI.Xaml fail-fast — see
crash-winui-stowed-exception memory — implicates exactly those control classes).

Stepped flow, one feature per screen, with Back/Next and a step/progress indicator:

1. **Welcome** — logo + one-line intro ("optiSYS keeps your PC fast and efficient, automatically.").
2. **Wi-Fi** — toggle (default on) + copy: "Keeps your connection responsive by stopping background
   Wi-Fi scans while you're online. Fully reversible — no permanent system changes."
3. **Battery** — toggle (default on) + copy: "Saves power when you're on battery and steps back the
   moment you plug in — automatically. Nothing to manage."
4. **Memory** — toggle (default on) + level dropdown (Balanced / Max) + copy: "Frees up memory when
   your PC is under pressure. 'Max' reclaims more, more often."
5. **Done** — "You're all set." Finish writes choices → `Settings`, sets
   `HasCompletedOnboarding = true`, persists, hides the layer, reveals the dashboard.

State: a pure, testable `OnboardingState` (or VM) holds current step index + the three choices and
maps them to `Settings` fields:
- Wi-Fi → `WiFiOptimizerEnabled`
- Battery → `CpuParkingEnabled` (the auto recommended↔saver switch in `AppRuntimeCoordinator`
  already runs independently and is unaffected)
- Memory → `AutoOptimizeMemoryEnabled` + `OptimizationLevel` (Balanced/Aggressive)

XAML is thin glue over that state. Navigation (Next/Back/Finish) and the choice→Settings mapping are
unit-tested; the panels themselves are verified by running the app.

## Settings restructure

Remove the in-app battery preset control (`BatteryPresetComboBox`, `OnBatteryPresetChanged`,
`SyncBatteryPresetSelection`, and the "Profile" row in the Efficiency card). Battery is purely
automatic now — `AppRuntimeCoordinator.OnPowerSourceChanged` keeps the recommended↔saver auto-switch.
No user-facing battery choice remains, per the owner's instruction. (The memory mode dropdown stays
on the Dashboard as today; onboarding just seeds its initial value.)

## Crash gate (applies to Part 2 only)

The onboarding is the most new XAML surface we'd add against the unconfirmed WinUI fault. Mitigations:
reuse the proven `Visibility`-toggle tab pattern; no dialogs/flyovers/NavigationView; verify by
running the app before committing. Part 1 (Inno) carries no WinUI crash risk.

## Version

Bump assembly version to 0.4.0 (UI reads it automatically; the installer's `AppVersion` define
follows via build-installer.ps1).

## Testing

- Core/unit: `OnboardingState` step navigation (Welcome→…→Done, Back/Next bounds), and
  choice→Settings mapping incl. memory level. TDD.
- Installer: built via `build-installer.ps1`; verified by running `setup.exe` (manual — Inno output
  is not unit-testable).
- App onboarding XAML + settings-restructure: verified by running the app (UI glue).
- Full `dotnet test` green before each commit.

## Open decisions for review

1. Install location under admin: keep `{localappdata}\Programs\optiSYS` (proposed) vs `Program Files`.
2. Whether closing the installer window (without clicking Launch) should also launch the app
   (brainstorm chose: Launch button is the explicit path; plain close does not auto-launch).
