# Tray icon redesign + memory-only tooltip — design & plan (2026-06-06)

## Goal
Rework the system-tray presentation:
- **Icon:** the **battery discharge rate (watts)** as the main, large number, with a small
  **efficiency-colored dot at the top-right** (the superscript / "x²" exponent position),
  green/yellow/red. Encodes battery load + efficiency, independent of charge %.
- **Tooltip:** memory usage only — `Memory: NN%`.

## Current state (what changes)
- Icon today: a single filled circle colored by efficiency (`TrayIconService.DotIconRenderer`,
  driven by `OverallHealthState` → `TrayDot`).
- Tooltip today: `optiSYS — Efficiency: Good/Normal/Bad`, built in `AppRuntimeCoordinator`
  (~line 182).
- **Battery drain rate is hardcoded `0`** in `WindowsNativeBridge.cs:77`; the real read
  (`GetBatteryState`/`SYSTEM_BATTERY_STATE.Rate`) was pruned as dead in 0.9. It must be revived
  for this feature to show a real number.

## Design

### Icon (32×32 source canvas, square)
- **Main element:** integer watts of battery discharge — large, centered, bold, theme-aware
  text color (reuse `SelectStrokeColor` light/dark).
- **Badge:** small filled circle at the top-right (exponent position), colored by efficiency,
  reusing the existing Green/Yellow/Red from the `OverallHealthState` → `TrayDot` mapping.
- **Value:** when on battery, `round(|DrainRateMilliwatts| / 1000)` → integer watts; when on AC,
  `0` (ignore the charging rate — `Rate` is positive while charging — regardless of charge %).
  Clamp display to 2 digits (`>= 100` shows `99`) so the number stays legible.
- **Re-render** only when the displayed number OR the efficiency color changes (extend the
  current dot-only change detection to (watts, color)).

### Tooltip
- `Memory: NN%`, `NN = round(usage percent)`. Replaces the efficiency string. Built from the
  memory info already available where the tray snapshot is assembled.

### Native: revive battery discharge rate (behind INativeBridge)
- Re-add a native read of the battery rate via the documented `CallNtPowerInformation`
  (`SystemBatteryState` → `SYSTEM_BATTERY_STATE.Rate`) in `NativeMethods.Power.cs`.
- Populate the bridge's battery info with the real `DrainRateMilliwatts` (replace the hardcoded
  `0`); log a native failure via the `IDiagnosticLog` seam and degrade to `0` when unavailable
  — consistent with the 0.9 Win32-error-logging pattern.
- Keep the existing convention: rate may be negative while discharging on some adapters; use
  magnitude (but only when on battery — see the AC gating above).
- **Side effect (intended):** `HealthScoreCalculator` already consumes `DrainRateMilliwatts`
  (fed `0` today); reviving the real rate makes the efficiency score — and thus the dot color —
  reflect actual drain. The calculator is already tested with non-zero drain
  (`HealthScoreCalculatorTests`), so this is the designed input going live, not new logic.
  Verify the green/yellow/red thresholds still read sensibly with real values.

## Data flow
`AppRuntimeCoordinator` (tray-update tick) → `TraySnapshot`:
- Extend `TraySnapshot` with `DischargeWatts` (keep `HealthState` for the efficiency color).
- Tooltip = `Memory: NN%` from the memory sample.
`TrayIconService` renders number + dot; re-renders on (watts, color) change.

## Testing (TDD)
- **Discharge formatting:** mW → watts rounding; `0` on AC; magnitude of negatives; 2-digit clamp.
- **Tooltip:** `Memory: 62%` formatting from a memory percent.
- **Change detection:** re-render fires when watts or efficiency color changes, not otherwise.
- **Native:** bridge battery read returns the real rate on success (mockable seam) and `0` +
  logged failure on failure (mirror the 0.9 Win32-logging tests).
- **Icon render:** `Render(watts, color)` returns a non-null 32×32 icon (GDI call exercised; the
  decision/state logic is the unit-tested part).
- Existing `TrayHealthEvaluator` / efficiency mapping tests stay green.

## Constraints / out of scope
- Tray service only — **no window-layout changes** (this is the authorized tray work).
- Display only — `no-user-setting-mutation` holds; no new user setting.
- Native-first: P/Invoke only in `NativeMethods.*`; all logic through the `INativeBridge` seam.

## Build sequence
1. **Native** — revive the battery-rate read behind `INativeBridge` (replace hardcoded `0`) +
   Win32/NTSTATUS failure logging + tests. (Core)
2. **Snapshot/data flow** — add `DischargeWatts` to `TraySnapshot`; `AppRuntimeCoordinator`
   computes watts + the `Memory: NN%` tooltip. (App)
3. **Icon renderer** — number-dominant with the top-right efficiency dot; (watts, color) change
   detection. (App)
4. **Tooltip** — memory-only. (App)
5. Build + full test suite green.
