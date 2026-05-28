# Native Windows UI/UX Overhaul — plan of record

Goal: make OptiSYS look and feel like a first-class native WinUI 3 / Fluent app.
Branch: `feat/native-ui-ux`. Verification per item: `build` green → `test` green →
launch smoke. **Smoke = startup.log must reach "window activated" with no _unhandled_
exception** (process-alive alone is NOT sufficient — the app's UnhandledException handler
can keep a broken process alive; and launching via automation hits a pre-existing,
app-handled `InvalidCastException` during WinUI activation that is benign). Visual QA happens
at milestone check-ins (no screenshot tooling this session).

Status legend: ☐ todo · ◐ in progress · ☑ done

## Milestone A — Theming & correctness ✅
- ☑ **#13** Set `AppWindow.Title = "optiSYS"` — alt-tab/taskbar show correct title.
- ☑ **#2** Register title-bar drag region (`SetTitleBar` under `ExtendsContentIntoTitleBar`) — window is draggable by the title strip.
- ☑ **#3** Replace per-tick `new SolidColorBrush(...)` in `UpdateDashboardUI` with resource-resolved/cached brushes — theme-reactive, zero per-tick allocation.
- ☑ **#7** Enforce min window size via `AppWindow.Changed` clamp (WinAppSDK 1.6 has no presenter min-size API) — cannot shrink below 800×560.
- ☑ **#4** Add `HighContrast` theme dict + skip accent override under HC + replace literal `#20FF0000` hover with `{ThemeResource SystemFillColorCriticalBackground}`.

## Milestone B — Accessibility ✅
- ☑ **#5** `AutomationProperties.Name` on the 5 nav buttons + both progress bars; delete buttons bound to the row item (+ "Remove" tooltip). Action buttons (`ManualTrimButton`, `ToggleProtectionBtn`) intentionally keep their changing `Content` as the name so Narrator announces state transitions.

## Milestone C — Layout robustness
- ☑ **#8** Exclusion/protected `ListView`s: replaced fixed `Height` with a `MinHeight`/`MaxHeight` band (160–420, 200–520) so short lists don't waste space and long lists cap-and-scroll.
- ☐ **#11** Chart grid lines width → **folded into #10** (HistoryChartControl extraction owns the canvas).

## Milestone D — Native navigation ✅ (needs visual QA)
- ☑ **#1** Replaced hand-rolled sidebar (5 Buttons + manual visibility + accent borders) with `NavigationView` (PaneDisplayMode=Left, no toggle/back, IsSettingsVisible=false) hosting the existing inline content. Status label + Pause/Resume moved to `PaneFooter`. Removed orphaned `SidebarButtonStyle`. New reflection tests lock `NavView` + the 5 page containers (134→140 tests).
- ☑ **#12** Persist & restore `SelectedNavItem` via `RestoreSelectedPage()` on startup + save-on-change in `SwitchToPage`. **Startup-crash fix:** initial `NavView.SelectedItem` assignment is deferred to the `Loaded` event — setting it during construction threw an unhandled `COMException 0x80070490` (caught later via the strengthened log-scan smoke).

## Milestone E — Code-behind health (TDD)
- ☐ **#6** Extract `ThemeManager` (theme/backdrop/accent/title-bar colours) out of `MainWindow.xaml.cs`.
- ☑ **#9** Deduplicated the 3 exclusion-CRUD blocks into shared `AddExclusion`/`RemoveExclusion` helpers; named methods kept as thin wrappers.
- ☑ **#10** Extracted `Controls/HistoryChartControl` (owns rolling buffer + canvas + redraw); MainWindow lost all `Canvas`/`PointCollection`/`_memoryHistory` code; chart is now `<controls:HistoryChartControl/>`. Includes **#11**: grid lines stretch to canvas width (no more `X2="2000"`).
