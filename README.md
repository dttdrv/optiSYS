# optiSYS

A native Windows system optimizer that keeps memory lean and the active network connection
responsive while staying out of the way. Every optimization is designed to be unnoticeable,
non-degrading, and reversible.

optiSYS is a WinUI 3 (Windows App SDK) application on .NET 9. It runs in the system tray,
monitors the machine continuously, and acts only when doing so actually helps.

## Design principles

optiSYS does not trade performance for savings or change how the machine behaves. It leaves the
foreground application and critical system processes untouched, never stops a service it tunes,
and can revert anything it does. Power-state changes that affect responsiveness — CPU, GPU, disk,
and USB power management — are deliberately out of scope.

## What it does

- Memory: continuous monitoring with a predictive trigger gated on real commit pressure, and a
  reclaim pipeline (working-set trim, standby and cache management, page combining) in two modes,
  Balanced and Max. The heavier steps run only under genuine pressure.
- Wi-Fi latency: disables the periodic background scan and enables streaming mode on the active
  adapter, removing the latency spikes Windows introduces while scanning on a live connection.
- Services: optionally sets a small, curated set of non-essential services to manual start —
  start type only, never stopped — behind a one-time elevation grant.

A single switch turns automatic optimization on or off. There is nothing to configure.

## Architecture

Optimization logic lives in `OptiSYS.Core` behind one contract, `IOptimizationDomain` (capture
baseline, apply, revert, report status). `UnifiedOptimizationEngine` composes the registered
domains and owns the snapshot/revert lifecycle. `OptiSYS.App` is the single-window WinUI shell,
the tray, and the runtime and automation services, wired through dependency injection.

| Project        | Role                                                                  |
| -------------- | --------------------------------------------------------------------- |
| `OptiSYS.Core` | Platform logic and the P/Invoke layer; fully testable, no UI.         |
| `OptiSYS.App`  | WinUI 3 single-window UI, system tray, and automation services.       |
| `OptiSYS.Tests`| xUnit, Moq, and FluentAssertions; mirrors the source layout one-to-one. |

## Install

Download the latest `optiSYS-<version>-setup.exe` from [Releases](../../releases) and run it. The
installer offers an optional one-time elevation for the deeper service optimization; everything
else runs without administrator rights.

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
