# Native Windows UI/UX Overhaul — plan of record

Goal: make OptiSYS look and feel like a first-class native WinUI 3 / Fluent app.
Branch: `feat/native-ui-ux`. Verification per item: `build` green → `test` green →
launch smoke. **Smoke = startup.log must reach "runtime coordinator started" with no
unhandled exception** (process-alive alone is NOT sufficient — the app's UnhandledException
handler can keep a broken process limping). The `InvalidCastException` ("No such interface
supported") the handler catches is NOT benign — it was the NavigationView (#1) failing to
render, crashing startup. A clean run reaches "runtime coordinator started"; a crashing run
stops at that exception. Visual QA happens at milestone check-ins (no screenshot tooling).

Status legend: ☐ todo · ◐ in progress · ☑ done · ❌ reverted — **12 of 13 shipped; #1 (NavigationView) reverted as incompatible with this build.** The app launches with a visible window; pending your visual QA + merge to `master`.

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

## Milestone D — Native navigation ❌ #1 REVERTED
- ❌ **#1** NavigationView was implemented, then **reverted**. In this project's stripped headless-PRI build its template fails to resolve a framework interface and throws an unhandled `InvalidCastException` (E_NOINTERFACE, "No such interface supported") during render → startup crash (`0xC000027B`). Confirmed by bisection: `master` (custom sidebar) launches cleanly with a window handle; the NavigationView branch crashes even with all my selection code disabled. Restored the hand-rolled sidebar (kept its #5 `AutomationProperties`). This is almost certainly why the app shipped a custom sidebar to begin with.
- ☑ **#12** Persist & restore `SelectedNavItem` — KEPT, adapted to the sidebar: `SwitchToPage` saves on change; the ctor calls `SwitchToPage(saved)` to restore (safe — only toggles Visibility + accent borders, no `NavigationView.SelectedItem`).

## Milestone E — Code-behind health (TDD) ✅
- ☑ **#6** Extracted `Services/ThemeManager` (theme/backdrop/accent/title-bar-button colours) out of `MainWindow.xaml.cs`; window now holds a `_theme` field and delegates. Faithful move, behaviour unchanged.
- ☑ **#9** Deduplicated the 3 exclusion-CRUD blocks into shared `AddExclusion`/`RemoveExclusion` helpers; named methods kept as thin wrappers.
- ☑ **#10** Extracted `Controls/HistoryChartControl` (owns rolling buffer + canvas + redraw); MainWindow lost all `Canvas`/`PointCollection`/`_memoryHistory` code; chart is now `<controls:HistoryChartControl/>`. Includes **#11**: grid lines stretch to canvas width (no more `X2="2000"`).
