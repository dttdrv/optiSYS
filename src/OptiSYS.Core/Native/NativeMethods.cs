namespace OptiSYS.Core.Native;

/// <summary>
/// P/Invoke declarations for Windows power management, process control,
/// service management, and memory APIs — the single P/Invoke layer for OptiSYS.Core.
///
/// Split across per-concern partials:
///   NativeMethods.Process.cs — process handles, EcoQoS / timer-resolution throttling, memory-priority reads
///   NativeMethods.Token.cs   — access tokens, privilege LUIDs (advapi32)
///   NativeMethods.Service.cs — Service Control Manager (advapi32)
///   NativeMethods.Power.cs   — power schemes, effective-power-mode signal, timer query (powrprof/ntdll)
///   NativeMethods.Memory.cs  — memory status, working-set / file cache, memory-list commands
///   NativeMethods.Window.cs  — foreground-window queries (user32)
/// </summary>
internal static partial class NativeMethods
{
}
