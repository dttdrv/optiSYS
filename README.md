<div align="center">

<img src="docs/assets/logo.png" width="120" alt="optiSYS" />

# optiSYS

**A native Windows system optimizer that keeps memory lean and your connection responsive — quietly, reversibly, and out of your way.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
&nbsp;![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
&nbsp;![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
&nbsp;![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0067C0)
&nbsp;![Version](https://img.shields.io/badge/version-1.0.0--alpha.1-orange)

</div>

---

optiSYS is a WinUI 3 (Windows App SDK) application on .NET 9. It lives in the system tray, monitors
the machine continuously, and acts only when doing so actually helps. Every optimization is designed
to be unnoticeable, non-degrading, and fully reversible.

## Screenshots

<div align="center">

<img src="docs/assets/screenshot-dashboard.png" width="85%" alt="optiSYS dashboard" />

<br /><br />

<img src="docs/assets/screenshot-settings.png" width="85%" alt="optiSYS settings" />

</div>

## What it does

- **Memory** — continuous monitoring with a predictive trigger gated on real commit pressure, and a
  reclaim pipeline (working-set trim, standby and cache management, page combining) in Balanced and
  Max modes. The heavier steps run only under genuine pressure.
- **Wi-Fi latency** — disables the periodic background scan on the active adapter, removing the
  latency spikes Windows introduces while scanning on a live connection.
- **Efficiency** — follows your power source and the Windows effective power mode, and never fights
  an explicit high-performance choice.
- **Services** — optionally sets a small, curated set of non-essential services to manual start
  (start type only, never stopped), behind a one-time elevation grant.

A single switch turns automatic optimization on or off. There is nothing to configure.

## Design principles

optiSYS never trades performance for savings or changes how the machine behaves. It leaves the
foreground application and critical system processes untouched, never stops a service it tunes, and
can revert anything it does. It does not mutate user-facing settings such as power mode, brightness,
or refresh rate — it reclaims and hints, transparently and reversibly.

## Install

Download the latest `optiSYS-<version>-setup.exe` from [Releases](../../releases) and run it. One UAC
prompt, then it installs itself and launches — no clicks. The deeper service optimization uses a
one-time elevation; everything else runs without administrator rights.

## Architecture

Optimization logic lives in `OptiSYS.Core` behind one contract — `IOptimizationDomain` (capture
baseline, apply, revert, report status). The engine composes the registered domains and owns the
snapshot/revert lifecycle. `OptiSYS.App` is the single-window WinUI shell, the tray, and the
runtime and automation services, wired through dependency injection.

| Project | Role |
| --- | --- |
| `OptiSYS.Core` | Platform logic and the single P/Invoke layer; fully testable, no UI. |
| `OptiSYS.App` | WinUI 3 single-window UI, system tray, and automation services. |
| `OptiSYS.Tests` | xUnit, Moq, and FluentAssertions; mirrors the source layout one-to-one. |

## Build from source

Requires the .NET 9 SDK on Windows (x64).

```
dotnet build src/OptiSYS.sln -c Debug
dotnet test  src/OptiSYS.sln -c Debug
dotnet run   --project src/OptiSYS.App
```

Building the installer additionally requires [Inno Setup 6](https://jrsoftware.org/isdl.php):

```
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1
```

## Requirements

Windows 10 (build 1809 or later) or Windows 11, x64.

## License

Released under the MIT License. See [LICENSE](LICENSE).
