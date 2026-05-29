using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;

namespace OptiSYS.Core.Native;

/// <summary>
/// Production <see cref="IServiceConfigStore"/> over the SCM P/Invokes. Read needs only
/// SERVICE_QUERY_CONFIG; write needs SERVICE_CHANGE_CONFIG (admin). All handles are closed; any
/// failure (incl. ERROR_ACCESS_DENIED when unelevated) surfaces as null/false, never an exception.
/// </summary>
public sealed class ServiceConfigStore : IServiceConfigStore
{
    public uint? GetStartType(string serviceName)
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var svc = NativeMethods.OpenServiceW(scm, serviceName, NativeMethods.SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) return null;
            try { return ReadStartType(svc); }
            finally { NativeMethods.CloseServiceHandle(svc); }
        }
        finally { NativeMethods.CloseServiceHandle(scm); }
    }

    public bool SetStartType(string serviceName, uint startType)
    {
        var scm = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = NativeMethods.OpenServiceW(scm, serviceName, NativeMethods.SERVICE_CHANGE_CONFIG);
            if (svc == IntPtr.Zero) return false;
            try
            {
                // SERVICE_NO_CHANGE for everything except the start type — never touches the binary
                // path, account, dependencies, etc.
                return NativeMethods.ChangeServiceConfigW(svc,
                    NativeMethods.SERVICE_NO_CHANGE, startType, NativeMethods.SERVICE_NO_CHANGE,
                    null, null, IntPtr.Zero, null, null, null, null);
            }
            finally { NativeMethods.CloseServiceHandle(svc); }
        }
        finally { NativeMethods.CloseServiceHandle(scm); }
    }

    private static uint? ReadStartType(IntPtr serviceHandle)
    {
        NativeMethods.QueryServiceConfigW(serviceHandle, IntPtr.Zero, 0, out var bytesNeeded);
        if (bytesNeeded == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!NativeMethods.QueryServiceConfigW(serviceHandle, buffer, bytesNeeded, out _))
                return null;
            // QUERY_SERVICE_CONFIGW: dwServiceType @0, dwStartType @4.
            return (uint)Marshal.ReadInt32(buffer, 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
