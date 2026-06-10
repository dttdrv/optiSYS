using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Window/input queries (user32.dll): foreground process resolution and system-wide user-idle
/// time (the read-only signal behind the idle deep-saver).
/// </summary>
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;   // tick count of the last input event, any session input device
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// Time since the last user input (keyboard/mouse), system-wide. Zero when the query fails —
    /// "user present" is the safe default (never deepen savings on a guess). Tick-count wrap is
    /// handled by the unsigned subtraction (correct across the 49.7-day rollover).
    /// </summary>
    internal static TimeSpan GetUserIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var elapsedMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }
}
