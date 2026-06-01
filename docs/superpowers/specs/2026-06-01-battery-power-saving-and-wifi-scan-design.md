# Battery power-saving (auto on DC) + always-on Wi-Fi background-scan-disable

Date: 2026-06-01
Status: Approved (design), pending implementation

## Context

Earlier this session two changes were made for safety:
- The Wi-Fi optimizer was made strictly opt-in (commit 92f226a) because media-streaming
  mode measurably added latency on the dev's Qualcomm adapter.
- CPU parking was made opt-in (commit 573353d) because it mutates the user-facing
  "Minimum Processor State" power-plan setting (the project's no-user-setting-mutation rule).

The project owner has now **explicitly relaxed the no-user-setting-mutation rule for the
battery category**: on battery, OptiSYS may lower the minimum processor state to 0%, cap the
maximum processor state (~15% reduction → 85%), and apply aggressive core parking — provided
every change is captured and cleanly reverted on AC / exit / crash. The owner also requires
Wi-Fi background-scan to be **always off when connected** (not opt-in, not battery-gated).

This spec records that relaxation and the resulting changes. The app runs elevated at all times,
so admin is not a constraint.

## Goals

1. On battery (DC), automatically apply a CPU power-saving profile; on AC / exit / crash,
   restore the exact captured values.
2. The CPU profile = minimum processor state 0% + maximum processor state 85% + aggressive
   core parking, all on the active scheme's DC values.
3. Wi-Fi background-scan-disable is always active while connected; media-streaming mode stays
   OFF (it was the harmful half).

## Non-goals (YAGNI)

- RAPL / package-power (PL1/PL2) limits — fragile, chip-specific capture/revert; deferred to
  OptiSYS.Lab for measurement before it is ever considered for shipping.
- Wi-Fi diagnostic opcodes (realtime quality / RSSI / channel) — separate Lab probe work.
- EPP / power-slider / brightness / refresh — remain DISALLOWED even under the relaxation.

## Design

### A. CpuParkingDomain — extend and re-enable

The domain already: captures `minProcessorState`, `maxProcessorState`, `coreParkingThreshold`
in its baseline; writes min (to `CpuParkingMinProcessorDC`, default 0) and core-parking (100)
in Apply; restores min + parking in Revert. The max state is captured but never written.

Changes:
- **Apply**: also write `GUID_PROCESSOR_THROTTLE_MAXIMUM` (DC) = `CpuParkingMaxProcessorDC`.
- **Revert**: also restore `maxProcessorState` from the baseline (baseline already records it).
- **Settings**: add `int CpuParkingMaxProcessorDC = 85`, clamped 50–100 in `Validate()`.
- **Settings**: `CpuParkingEnabled` default flips `false → true` (re-enables auto-on-battery).

Activation is unchanged and already correct: `PowerSourceMonitor.PollCallback` calls
`ActivateCategory("Battery")` on AC→DC and `RevertDomain("cpu-parking")` on DC→AC. With the
flag true, the engine's `IsDomainEnabled` gate lets it run. `QuietAutomationService` must no
longer be relied on to force it (the force-set was removed in 573353d and stays removed); the
domain runs purely from its saved flag + the power-source monitor.

Status summary string updated to mention the max cap.

### B. Wi-Fi — split the two opcodes

- `WiFiOptimizerEnabled` default `false → true`.
- `WiFiDisableBackgroundScan` stays `true`.
- `WiFiStreamingMode` stays `false` (harmful half; do not enable).

The optimizer already activates at startup via `QuietAutomationService.SetOptionalOptimizations`,
holds the WLAN handle open, and reapplies every 45s — so background scan is off whenever
connected, independent of power source. No new activation code needed; only the default flip.

The reapply interval (45s, set this session) is unchanged.

## Reversibility / safety

- All CPU changes are DC power-scheme values written via `PowerWriteDCValueIndex` and restored
  to exact captured indices on DC→AC, app exit, and crash recovery (SnapshotStore replays the
  baseline). Max state restoration closes the one gap where the captured value was previously
  ignored.
- Open risk (carried from battery research): a crash between Apply and Revert leaves min/max
  modified until the next run reverts from the stored snapshot. SnapshotStore persistence covers
  this; crash-recovery path should be verified during implementation (read snapshot → Revert,
  not re-capture).
- Wi-Fi opcodes remain session-handle-scoped and auto-revert when the handle closes.

## Testing (TDD, Core)

1. CpuParkingDomain: baseline captures max state; Apply writes `CpuParkingMaxProcessorDC`;
   Revert restores the captured max state. (Use the existing native seam / fake.)
2. Settings: `CpuParkingEnabled` defaults true; `WiFiOptimizerEnabled` defaults true;
   `WiFiStreamingMode` defaults false; `CpuParkingMaxProcessorDC` defaults 85 and clamps 50–100.
3. Update existing tests that asserted the opt-in defaults from commits 92f226a / 573353d
   (SettingsTests, QuietAutomationServiceTests, UnifiedOptimizationEngine gating test).
4. Full suite green before commit.

## Rollout note

This reverses the two opt-in defaults set earlier in the SAME session, intentionally, under the
owner's explicit rule relaxation. Memory ([[no-user-setting-mutation]], [[battery-domains-findings]],
[[wifi-optimizer-opt-in-and-internet-diagnosis]]) must be updated so the reversal is not later
"corrected" back. Live settings.json on the dev machine is updated to match.
