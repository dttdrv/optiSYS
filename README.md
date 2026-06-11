<div align="center">

<img src="assets/logo.png" width="120" alt="optiSYS" />

# optiSYS

**A native Windows optimizer for memory, battery, and latency. Automatic, reversible, silent.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
&nbsp;![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)
&nbsp;![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)
&nbsp;![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0067C0)
&nbsp;![Version](https://img.shields.io/badge/version-1.0.0-2EB84C)

</div>

optiSYS runs in the system tray and keeps your machine fast and your battery lasting longer.
It works automatically, everything it does is reversible, and it never changes a Windows setting
you can see. One switch, nothing to configure.

## Screenshots

<div align="center">

<img src="assets/screenshot-dashboard.png" width="85%" alt="optiSYS dashboard" />

<br /><br />

<img src="assets/screenshot-settings.png" width="85%" alt="optiSYS settings" />

</div>

## What it does

- **Battery.** Background apps that waste CPU are switched into Windows efficiency mode, and noisy
  processes are kept from blocking the CPU's deep sleep. Your active app, anything playing audio,
  and important programs are never touched. In a game or high-performance mode, optiSYS steps aside.
- **Memory.** Frees memory before pressure builds, without slowing down what you are using.
- **Wi-Fi.** Optionally removes the lag spikes caused by Windows scanning for networks in the
  background.

## Install

Download the latest `optiSYS-<version>-setup.exe` from [Releases](../../releases) and run it.
One UAC prompt, then it installs and launches on its own. Requires Windows 10 (build 1809 or
later) or Windows 11, x64.

## Build from source

Requires the .NET 9 SDK on Windows (x64).

```
dotnet build src/OptiSYS.sln -c Debug
dotnet test  src/OptiSYS.sln -c Debug
dotnet run   --project src/OptiSYS.App
```

## License

Released under the MIT License. See [LICENSE](LICENSE).
