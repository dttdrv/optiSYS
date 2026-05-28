# OptiSYS Overhaul — Design & Phased Implementation Plan

> Produced by the `optisys-overhaul-research` dynamic-workforce run (14 agents: codebase map →
> web research → adversarial safety verification → synthesis). Design only — no implementation
> until the OPEN DECISIONS (§c) are settled.

**Scope:** A WinUI 3 (Windows App SDK 1.6, unpackaged win-x64) tray utility. Hard user constraint:
**must not hinder any workflow or performance, and every change must be fully reversible.** That
constraint is the single arbiter for ship-by-default vs opt-in vs rejected. All system-mutating work
flows through the existing `IOptimizationDomain` capture→apply→revert lifecycle, is registered in
`AppHost.ConfigureServices`, gated by a `Settings` flag + a `UnifiedOptimizationEngine.IsDomainEnabled`
case, and snapshotted via `SnapshotStore` for crash recovery.

## Grounding facts verified in-repo
- **Engine has switch cases with no registered domain.** `IsDomainEnabled` handles `background-services`,
  `usb-suspend`, `network-power`, `gpu-power`, `cpu-parking`, `disk-coalescing`, but `AppHost` only
  registers `EcoQosDomain`, `TimerResolutionDomain`, `MemoryOptimizerDomain`. Adding a domain = register
  in AppHost + add Settings flag + add switch case.
- **EcoQoS throttles EVERY non-foreground process** (`EcoQosDomain.cs:66-96`), not "idle background" ones
  → confirms EcoQoS + TimerResolution should be **opt-in, not on-by-default**.
- **The harmful memory sequence is real but not default-reachable.** `MemoryOptimizer.OptimizeAll` chains
  destructive steps at Balanced/Aggressive, but `OptimizationLevel` defaults to `Conservative`.
- **Native present:** `SetProcessMemoryPriority`, `MEMORY_PRIORITY_*`, `SetSystemFileCacheSize`, powercfg
  read/write **DC** + read **AC**. **Native MISSING:** all WLAN P/Invoke (`wlanapi.dll`),
  `PowerWriteACValueIndex` (only Read exists), overlay-scheme APIs, any tray number rendering.
- **UI today:** `App.xaml` hardcodes green `#5CB865`; `BackdropType="MicaAlt"`, `UseWindowsAccentColor=false`,
  `ThemeMode="Dark"`. `MainWindow.xaml` is a single-file 5-tab sidebar using `{ThemeResource}` throughout.
- **app.manifest:** `asInvoker` (no elevation).

---

## 1. WiFi Optimizer Domain (`wifi-optimizer`)
Latency tweaks via the session-scoped `wlanapi.dll` Native WiFi API — no registry, no reboot, reversible.
**Reject autoconfig-disable** (breaks WiFi UI, revert ambiguity) and **reject Intel registry ScanDisable**
(reboot + bricking risk).
- New `Domains/Network/WiFiOptimizerDomain.cs` (`Category="Network"`, supported = Wlansvc + a WLAN interface)
  and `Native/WlanNativeMethods.cs` (`WlanOpenHandle/Close/EnumInterfaces/Query/SetInterface/Free/RegisterNotification`;
  `WLAN_INTF_OPCODE` background_scan=2, media_streaming_mode=3 — verify ordinals vs `wlanapi.h`).
- Background Scan → OFF, Streaming Mode → ON (the two you enabled); AutoConfig never written.
- Connected-only (else `ERROR_INVALID_STATE`); session-scoped so **re-apply on reconnect** via
  `WlanRegisterNotification`; **verify-after-write** (some Intel adapters report success but ignore it);
  revert restores captured per-interface booleans then closes the handle.
- Settings: `WiFiOptimizerEnabled=false` (opt-in), `WiFiDisableBackgroundScan=true`, `WiFiStreamingMode=true`.
- **Default OFF**; single "Undo all WiFi tweaks" action; show live queried state.

## 2. Services-to-Manual Domain (`services-manual`)
Flip a vetted set to demand-start (`Start=3`), snapshot exact prior `StartType` for revert. **Requires admin.**
- New `Domains/Services/ServiceManualDomain.cs` (`Category="System"`), reusing `BackgroundServiceDomain`'s SCM
  P/Invoke.
- **Safe-by-default (6):** `MapsBroker`, `Fax`, `lltdsvc`, `wisvc`, `XblAuthManager`, `XblGameSave` — but
  several are *already* Manual → detect current `Start`, skip no-ops, report "already optimal".
- **Opt-in (advanced, off, several hardware-gated):** `TrkWks`, `PcaSvc`, `Spooler`*, `DiagTrack`, `DoSvc`,
  `lfsvc`, `SSDPSRV`, `SharedAccess`, `QWAVE`, `RmSvc`, `PhoneSvc`, `ShellHWDetection`, `SysMain`*, `WiaRpc`,
  `stisvc`, `icssvc`, `RemoteRegistry`. (*printer/SSD+RAM gated.)
- **Hard block-list (never touched, even via config):** `Audiosrv`, `AudioEndpointBuilder`, `RpcSs`,
  `RpcEptMapper`, `DcomLaunch`, `MpsSvc`, `Dhcp`, `Dnscache`, `NlaSvc`, `Wcmsvc`, `WinHttpAutoProxySvc`
  (hard-block `Start=4` on 24H2+), `WinDefend`, `SystemEventsBroker`, `TimeBrokerSvc`, `Themes`, `Schedule`,
  `bthserv`, `WpnService`, `BFE`, `SamSs`, `RasMan`, `EapHost`.
- Never STOP a running service (Manual takes effect next demand/boot); recommend a System Restore point
  before first apply; skip per-user template services; warn (don't fight) on Intune/AzureAD-managed machines.
- Settings: `ServicesManualEnabled=false`, `ServicesManualOptIn=[]` (validated against the allow-list).
- Recommended as an explicit one-time "Apply safe service tune-up" action, not silent background activation.

## 3. Memory Optimization Upgrade
Principle: continuous = only the no-disk-IO eviction hint; anything writing disk / purging cache / forcing a
scan / system-wide trimming is pressure-gated on-demand or rejected as a default.
- **ADD safe-by-default:** `ProcessMemoryPriority` hints (`MEMORY_PRIORITY_LOW/VERY_LOW`) on a **curated
  known-background allowlist** (indexers/updaters/sync) — pure eviction-order hint, zero disk IO, reversible.
- **KEEP but pressure-gate (opt-in/on-demand):** `FlushModifiedList`; low-priority standby purge (threshold
  gated); `SetSystemFileCacheSize` (on-demand, ≥10% floor).
- **REJECT as automatic:** `EmptySystemWorkingSets()` system-wide trim; full `PurgeStandbyList()`; forced
  `CombinePhysicalMemory()` every Balanced pass → **remove the unconditional Combine call**; move full purge +
  system-empty to a manual **"Deep clean now"** button only.
- Conservative stays the only default-on tier; Aggressive becomes explicit opt-in with an in-UI warning.
- **Flip `EcoQosEnabled`/`TimerResolutionEnabled` defaults `true → false`** (they throttle all non-foreground
  processes); add a curated allowlist before they could be considered safer.

## 4. Battery / Power Domains — AC vs DC aware, reversible
Native prereq: add `PowerWriteACValueIndex` + `WriteACValue`/`ReadACValue`. Reversibility via `powercfg /export`
of the active scheme first (or capture per-setting AC+DC indices).
- **`PowerBaselineDomain` (safe, default ON on battery devices):** `PERFAUTONOMOUS=1` (AC+DC, HWP/CPPC, no
  throughput cost); leave system EcoQoS enabled (do not set `PowerThrottlingOff`); moderate DC display/sleep
  timeouts (monitor 3–5 min, sleep 10–15 min — not the aggressive 2/5); read-only diagnostics
  (`/energy`, `/batteryreport`, `/sleepstudy`).
- **`PowerAdvancedDomain` (opt-in, default OFF):** overlay "Better Battery" on DC; `PERFEPP` 50–60 DC;
  `PERFBOOSTMODE=3` DC; PCIe ASPM L1 DC (instability risk → Moderate first); USB selective suspend DC;
  adaptive brightness/hibernate; wake timers = "Important only" (never 0); Modern-Standby network = Managed;
  passive cooling DC; Battery Saver threshold.
- **REJECT:** Max Processor State cap (hinders sustained workloads; and `PROCTHROTTLEMAX1` is hybrid-only —
  **never write on this AMD non-hybrid CPU**); WiFi adapter Max Power Saving mode 3 (PSM latency breaks
  calls/games/VPN) → cap at Medium, opt-in only.
- Reuse `IPowerSourceMonitor` for AC↔DC; detect hybrid vs non-hybrid at runtime; all changes on a duplicated
  scheme + exported `.pow` backup; re-verify after Windows updates (24H2/25H2 may reconcile overlay/EPP).

## 5. UI Overhaul
- **Follow system accent:** remove hardcoded green from `App.xaml` theme dictionaries; flip
  `UseWindowsAccentColor false → true`; subscribe `UISettings.ColorValuesChanged` (marshal to
  `DispatcherQueue.TryEnqueue` — fires off-thread), write `SystemAccentColor` + the 6 Light/Dark shades; poll
  once at startup; use `{ThemeResource}` for accent.
- **Remove pure-black/OLED:** keep MicaAlt (warm grey, not `#000000`); the flat-black is a
  `SystemBackdropConfiguration.Theme` not tracking `ActualTheme` bug → use XAML `<MicaBackdrop Kind="BaseAlt"/>`
  on `Window.SystemBackdrop` (SDK wires theme internally) instead of a hand-managed controller; non-Mica
  fallback = `SolidBackgroundFillColorBase` (`#202020`, never pure black) guarded by `MicaController.IsSupported()`;
  dark title bar via `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`.
- **"A non-technical parent could use it":** Dashboard becomes the whole app for normal users — one big health
  number (the tray score), a plain-language status line, one **"Optimize now"** button; per-domain knobs move
  under a collapsed **"Advanced"** disclosure; replace jargon ("EcoQoS/Standby Purge/Working Set" → "Battery
  saver for background apps", "Free up memory"); each toggle gets a one-line plain description.
- **Smoother/native:** entrance/connected animations on tab switches; `ProgressRing` for the score; `InfoBar`
  for applied/reverted/ignored; `TeachingTip` first-run; respect Reduced Motion.

## 6. Unintrusive Admin Elevation — recommended approach
**`asInvoker` main process + on-demand elevated helper relaunch** (the Task Manager / Disk Cleanup model).
- Keep `app.manifest` at `asInvoker`; tray UI, user-process memory trims, and all telemetry run unelevated
  (also preserves Explorer drag-drop, which an elevated process loses via UIPI).
- For admin-needing ops (services-manual, powercfg writes, system-process EcoQoS) relaunch a minimal helper:
  `ProcessStartInfo { Verb = "runas", Arguments = "--elevated --op ..." }`; helper does the op, writes result
  JSON (named pipe or `%TEMP%`), exits; UI reads after `WaitForExit`; catch `Win32Exception 1223` (declined
  UAC) → friendly "needs admin" InfoBar.
- Every privileged domain checks `IsElevated` and **degrades gracefully** (skip + banner) when unelevated, so a
  user who never clicks "yes" still has a fully working app with the safe-by-default set.
- Rejected: `requireAdministrator` (UAC on every autostart; breaks drag-drop); Task Scheduler task (viable but
  install-time complexity + GP-deletion fragility — held as a future option).

## 7. Tray Optimization SCORE (0–100)
Extend `TraySnapshot` (`Models/HealthModels.cs`) with `int Score`; feed `CreatePulseIcon`
(`TrayIconService.cs:264`) to render the number. A `HealthScoreCalculator` fed from `IMemoryInfoService` +
`IBatteryInfoService`.

Inputs (each 0–100, higher = healthier): `MemHeadroom = 100 − usedPercent`; `MemSavings = min(100,
freedMB/targetMB*100)` (target ≈ 10% of RAM, rolling); `BattLevel` = charge %; `BattSavings` = drain-rate
improvement vs a rolling AC/DC baseline.

- **On battery (DC):** `Score = 0.30·MemHeadroom + 0.20·MemSavings + 0.30·BattLevel + 0.20·BattSavings`
- **Plugged in / no battery (AC):** `Score = 0.60·MemHeadroom + 0.40·MemSavings` (battery terms drop — satisfies
  "plugged in = only memory + memory savings").

Clamp `[0,100]`, round. Color bands reuse `OverallHealthState`: `<40` red, `40–59` orange, `60–74` normal,
`75–89` good, `90+` accent. Recompute on the 5s tick but only `NIM_MODIFY` when the integer or band changes.
Render: DPI-aware bitmap (`SM_CXSMICON`), 2-digit `Segoe UI Variable` bold ~65% height, transparent bg, band
color; **GDI discipline** — `DestroyIcon` the previous HICON, dispose Bitmap/Graphics each cycle. "100" → "OK"
or a filled ring (3 digits won't fit 16px).

---

## (a) Phased rollout
- **Phase 0 — Foundations (no system mutation):** add Network/System categories + Settings flags + engine
  switch cases (no-op until domains land); add native P/Invokes (`wlanapi`, `PowerWriteACValueIndex`); add
  `HealthScoreCalculator` + `TraySnapshot.Score`; snapshot round-trip tests.
- **Phase 1 — UI overhaul (lowest risk, no privilege):** system accent (drop green); Mica theme-tracking fix +
  non-black fallback; simple-mode Dashboard + Advanced disclosure; tray score rendering.
- **Phase 2 — Memory correctness (no new privilege):** flip EcoQoS/TimerResolution defaults OFF; remove forced
  Combine from Balanced; pressure-gate standby/flush; add ProcessMemoryPriority allowlist; Aggressive → opt-in +
  "Deep clean now" manual button.
- **Phase 3 — WiFi domain (unelevated, session-scoped, reversible).**
- **Phase 4 — Elevated domains (on-demand helper):** ServiceManualDomain → PowerBaselineDomain →
  PowerAdvancedDomain; admin + System Restore prompt; graceful unelevated degradation.
- **Phase 5 — Polish:** crash-recovery verification, Reduced-Motion, first-run onboarding, post-update re-verify.

## (b) Opt-in (ship OFF) vs safe-by-default (ON)
- **OFF:** entire WiFi domain; entire Services domain + all advanced extras; Aggressive memory + full
  purge/system-empty (manual button only) + file-cache cap; entire `PowerAdvancedDomain`;
  `EcoQosEnabled`/`TimerResolutionEnabled` (flipped to OFF).
- **ON:** follow-system-accent + Mica UI; Conservative memory + ProcessMemoryPriority hints; `PowerBaselineDomain`
  (PERFAUTONOMOUS, EcoQoS-default, moderate DC timeouts, diagnostics). The 6 safe services are *eligible* for
  default but recommended as a one-time admin-gated action, not silent activation.

## (c) DECISIONS — RESOLVED (2026-05-29)
1. **Services default:** ✅ **Silently apply the 6 safe services when admin is granted** (detect + skip
   no-ops; snapshot prior Start for revert).
2. **Elevation model:** ✅ **Silent-on-logon via a Task Scheduler highest-privileges task that the APP
   provisions itself** — one-time UAC at first admin-use registers the task; every logon after is elevated &
   silent. Graceful unelevated degradation if the user declines the one-time setup.
3. **EcoQoS / TimerResolution:** ✅ **Flip both defaults to OFF** (opt-in; they throttle all non-foreground
   processes).
4. **Aggressive memory:** ✅ **Opt-in + warning, plus a manual "Deep clean now" button.** Conservative stays
   the default; the destructive path is never auto-reachable.
5. **Score formula:** ✅ Proceed with proposed weights (DC 30/20/30/20, AC 60/40); tunable later.
6. **Categories:** ✅ **Fold** — WiFi under "Power & Energy", Services under "Settings → Advanced" (simpler;
   fits the non-technical-user goal). No new top-level sidebar categories.
7. **Min Windows build:** ✅ **Windows 11 (22000+)** is the floor.
