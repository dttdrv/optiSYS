using System.Runtime.InteropServices;
using System.Security.Principal;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Manages Windows privilege escalation for optimization operations.
/// Ported from optiRAM.
/// </summary>
public static class PrivilegeManager
{
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    /// <summary>
    /// True when the current process token is a member of the local Administrators role
    /// (i.e. we hold an elevated token). This is the single source of truth that
    /// admin-gated domains check before attempting HKLM / SCM writes — when false they
    /// skip and report "needs admin" rather than failing with ERROR_ACCESS_DENIED.
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool EnablePrivilege(string privilegeName)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out var tokenHandle))
            return false;

        try
        {
            var luid = new NativeMethods.LUID();
            if (!NativeMethods.LookupPrivilegeValueW(null, privilegeName, out luid))
                return false;

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                }
            };

            if (!NativeMethods.AdjustTokenPrivileges(tokenHandle, false, tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }
    }

    /// <summary>
    /// Enable all required privileges. Returns true only if ALL privileges were enabled.
    /// </summary>
    public static bool EnableAllRequired()
    {
        bool debug = EnablePrivilege(NativeMethods.SE_DEBUG_NAME);
        bool profile = EnablePrivilege(NativeMethods.SE_PROFILE_SINGLE_PROCESS_NAME);
        bool quota = EnablePrivilege(NativeMethods.SE_INCREASE_QUOTA_NAME);
        return debug && profile && quota;
    }
}
