using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Window queries (user32.dll) used to resolve the foreground process id.
/// </summary>
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
