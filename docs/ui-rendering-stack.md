# optiSYS UI and rendering stack

This note records the Microsoft Store-inspired libraries that fit optiSYS' current app shape.

## Current app shape

optiSYS is a native C# WinUI 3 desktop app:

- `TargetFramework`: `net9.0-windows10.0.26100.0`
- UI platform: Windows App SDK / WinUI 3
- Packaging mode: unpackaged/self-contained Windows App SDK executable

## Adopted for UI/rendering

| Library | Why it fits optiSYS |
| --- | --- |
| Windows App SDK / WinUI 3 | Native Windows UI foundation already used by the app. This is the correct WinUI track for a desktop app. |
| Microsoft.Windows.CsWinRT | Already used for WinRT interop from C#. Keep it for Windows API projection. |
| CommunityToolkit.Mvvm | Store-used MVVM foundation. Use it when the next UI pass reintroduces presentation models and commands. |
| CommunityToolkit.WinUI.Controls.Primitives | WinUI 3-compatible Windows Community Toolkit controls and layout primitives. Useful for native Fluent UI without custom control reinvention. |
| Win2D via ComputeSharp.D2D1.WinUI | GPU-backed Direct2D rendering and C# pixel shader effects. Use for real graphics, effects, and custom rendering surfaces. |
| System.Reactive | Useful for composing UI-facing telemetry streams such as memory, battery, power-source, and automation state updates. Keep it in the app layer, not core. |

## Not adopted for this app layer

| Library | Decision |
| --- | --- |
| WinUI 2 | UWP track. optiSYS is WinUI 3 / Windows App SDK. |
| C++/WinRT | Useful for C++ components, but this app UI is C#. Keep existing CsWinRT unless a native C++ UI component is introduced. |
| Windows Implementation Library | C++ helper library. Not useful for C# XAML UI code. |
| PolySharp | Downlevel C# polyfills. optiSYS targets .NET 9, so this is unnecessary. |
| Polly | Good resilience library, but not a UI/rendering dependency. Add later only for network/update/download workflows. |
| Shmuelie.WinRTServer | AppServices/full-trust helper for UWP-style sandbox escapes. optiSYS is already an unpackaged desktop app with direct service access. |
| Windows Package Manager | Distribution/install workflow, not UI/rendering. |
| Windows Community Toolkit Labs | Experimental surface. Consider only for a specific control after the stable toolkit cannot cover it. |
