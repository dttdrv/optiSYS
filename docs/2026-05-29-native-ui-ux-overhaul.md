# Native Windows UI/UX Overhaul — plan of record

Goal: make OptiSYS look and feel like a first-class native WinUI 3 / Fluent app.
Branch: `feat/native-ui-ux`. Verification per item: `build` green → `test` green →
launch smoke. Visual QA happens at milestone check-ins (no screenshot tooling this session).

Status legend: ☐ todo · ◐ in progress · ☑ done

## Milestone A — Theming & correctness ✅
- ☑ **#13** Set `AppWindow.Title = "optiSYS"` — alt-tab/taskbar show correct title.
- ☑ **#2** Register title-bar drag region (`SetTitleBar` under `ExtendsContentIntoTitleBar`) — window is draggable by the title strip.
- ☑ **#3** Replace per-tick `new SolidColorBrush(...)` in `UpdateDashboardUI` with resource-resolved/cached brushes — theme-reactive, zero per-tick allocation.
- ☑ **#7** Enforce min window size via `AppWindow.Changed` clamp (WinAppSDK 1.6 has no presenter min-size API) — cannot shrink below 800×560.
- ☑ **#4** Add `HighContrast` theme dict + skip accent override under HC + replace literal `#20FF0000` hover with `{ThemeResource SystemFillColorCriticalBackground}`.

## Milestone B — Accessibility
- ☐ **#5** `AutomationProperties.Name` on nav buttons, icon-only delete buttons, action buttons, progress bars; delete buttons announce the row item.

## Milestone C — Layout robustness
- ☐ **#8** Exclusion/protected `ListView`s: drop fixed `Height` for stretch/`MaxHeight`.
- ☐ **#11** Chart grid lines: bind `X2` to canvas width instead of `X2="2000"`.

## Milestone D — Native navigation
- ☐ **#1** Replace hand-rolled sidebar with `NavigationView` (hosting existing inline content; no Page/Frame re-fragmentation).
- ☐ **#12** Persist & restore `SelectedNavItem` across sessions (folded into #1).

## Milestone E — Code-behind health (TDD)
- ☐ **#6** Extract `ThemeManager` (theme/backdrop/accent/title-bar colours) out of `MainWindow.xaml.cs`.
- ☐ **#9** Deduplicate the 3 exclusion-CRUD blocks into one generic helper.
- ☐ **#10** Extract `HistoryChartControl` (canvas + redraw) into its own control.
