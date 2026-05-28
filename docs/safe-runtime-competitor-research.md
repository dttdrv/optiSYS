# optiSYS safe runtime competitor research

Date: 2026-05-12

## Product rule

optiSYS is a background Windows runtime optimizer. It may apply conservative, reversible runtime actions, but it must not alter machine setup, user work state, shell behavior, services, drivers, devices, registry tweaks, app installs, Windows Update policy, or power-plan configuration.

Allowed automatic actions:

- Conservative memory cleanup with critical shell/process exclusions.
- EcoQoS on non-foreground, non-shell, non-protected background processes.
- Per-process timer-resolution ignore on supported Windows builds.
- Startup registration for optiSYS itself, using HKCU Run with `--background`.

Disallowed automatic actions:

- Service stop/start/configuration.
- USB, network, GPU, CPU parking, disk idle, or power-plan writes.
- App removal, debloat scripts, telemetry/privacy registry edits, Explorer restarts, file cleanup, cache deletion, and global timer-resolution changes.

## Competitor scan

| Tool | What it proves | Safe extraction for optiSYS |
| --- | --- | --- |
| Microsoft PC Manager | Official tools frame cleanup as quiet, reliable, and built around Windows facilities. | Keep optiSYS quiet, tray-first, and transparent about what it does. Avoid surprise cleanup or deletion. |
| Process Lasso | A serious optimizer splits GUI from background governor, exposes logs, and treats Efficiency Mode as heavy enough to default cautiously. | Keep a minimal foreground shell, background coordinator, action logs, exclusions, and conservative defaults. |
| Mem Reduct | Memory tools get value from monitoring plus explicit/manual or threshold cleanup, but deeper native cleanup needs admin and undocumented APIs. | Keep automatic cleanup conservative and exclude shell/work apps. Do not add admin-only deep memory purges by default. |
| ChrisTitusTech WinUtil | Large tweak catalogs are maintainable when data-driven, but they are intentionally mutating tools. | Reuse catalog thinking only for future manual recommendations, not automatic registry/service changes. |
| Hellzerg Optimizer / Winhance class tools | Debloat/performance apps often cover services, telemetry, updates, networking, HPET, OneDrive, and UWP removal. | Treat those as "manual review / do not auto-touch" categories. They are outside optiSYS automatic scope. |

## Adopted design principles

1. Safe runtime only

   Optimize the current runtime state. Do not rewrite Windows policy or user setup beyond optiSYS's own background launch.

2. Immutable exclusions

   Shell/session hosts and critical processes are protected in code, not only in user settings.

3. Foreground first

   Foreground apps and protected work apps are skipped. Optimization should improve idle/background behavior without stealing from active work.

4. No global timer mutation

   Timer optimization uses per-process ignore on supported Windows builds. Older builds skip safely.

5. Barebones native shell

   The UI is an engineer-style status window plus tray menu, not a dashboard suite.

## Sources

- Microsoft PC Manager: https://pcmanager.microsoft.com/en-us
- Microsoft Windows performance tips: https://support.microsoft.com/en-us/windows/tips-to-improve-pc-performance-in-windows-b3b3ef5b-5953-fb6a-2528-4bbed82fba96
- Microsoft EcoQoS: https://devblogs.microsoft.com/performance-diagnostics/introducing-ecoqos/
- Process Lasso docs: https://bitsum.com/processlasso-docs/
- Mem Reduct repo: https://github.com/henrypp/memreduct
- ChrisTitusTech WinUtil repo: https://github.com/ChrisTitusTech/winutil
- Hellzerg Optimizer repo: https://github.com/hellzerg/optimizer
