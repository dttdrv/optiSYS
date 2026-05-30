# UI overhaul — tray dot, heavy-apps, sidebar About, polish (2026-05-30)

Approved design for the UI/UX pass requested 2026-05-30.

## A. Tray icon → colored dot (3 states) + efficiency tooltip
- Replace the numeric `ScoreIconRenderer` output with a filled circle.
- Color from `TrayHealthEvaluator` (`OverallHealthState`), collapsed 5→3:
  - **Green** = Great, Good
  - **Yellow** = Normal
  - **Red** = NotGood, Bad, Paused
- Tooltip drops memory/battery; reads `optiSYS — Efficiency: Good|Normal|Bad` (same mapping).
- The health→(color, label) mapping is a pure function — **TDD**.

## B. Dashboard → memory-heavy apps card
- New card below the Memory/Efficiency row: processes with working set **> 1 GB**, name + size, **top 5**, descending. Hidden when none qualify.
- Source: native process list (name + working-set bytes). Selection (filter > 1 GB, sort desc, take 5) is a pure function — **TDD**.
- Refreshed on a ~5 s throttle inside the 1 s UI refresh (enumeration isn't free).

## C. Sidebar
- Width **212 → 180 px** (−15%).
- About block at the **bottom-left** of the sidebar: app name, version (assembly), "Deyan Todorov" (dttdrv.xyz), minimal **GitHub** link (`https://github.com/dttdrv/optiSYS`).
- Remove the About card from the Settings page.

## D. Remove Deep Clean
- Delete the Settings Deep Clean card + `DeepCleanButton` + `OnDeepCleanClick`.
- Remove the now-orphaned `RunDeepCleanAsync` from `IQuietAutomationService` + `QuietAutomationService` + its test + the test-stub impl. (Tray "Optimize now" uses the normal cleanup, unaffected.)

## E. Polish
- **Switches flush-right:** `ToggleSwitch` `MinWidth=0` + right-align so the pill sits flush and the three align.
- **Chart grey line:** remove `GridLine1/2/3` (the dashed helper lines showing above the curve) from `HistoryChartControl` XAML + their `Redraw` code.
- **Scrollbar:** right-padding on scroll content + clean overlay scrollbar so it stops colliding with card edges. Verify visually by running.

## Testing
- TDD (Core/App logic): health→(color, efficiency-label) mapping; heavy-process selection.
- UI-glue (XAML layout, sidebar, switches, scrollbar, gridlines, tray bitmap, About): verify by building + running the app (startup reaches "runtime coordinator started", visual check).
- `build` + `test` green before done.

## Out of scope
- The "just disappears" crash (separate, open).
- Memory/battery optimization behavior (unchanged).
