using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Stops non-essential Windows services while on battery.
/// Targets: indexing, telemetry, prefetch, update services.
/// Each service's original state is captured and restored exactly.
/// </summary>
public sealed class BackgroundServiceDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private bool _isActive;
    private int _servicesStopped;

    public string Id => "background-services";
    public string DisplayName => "Background Services";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.BackgroundServicesEnabled;

    public BackgroundServiceDomain(Settings settings) { _settings = settings; }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        var serviceStates = new Dictionary<string, ServiceState>();

        var servicesToThrottle = Settings.NormalizeServicesToThrottle(_settings.ServicesToThrottle);
        var scManager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scManager == IntPtr.Zero)
        {
            snapshot.Set("services", serviceStates);
            return snapshot;
        }

        try
        {
            foreach (var serviceName in servicesToThrottle)
            {
                var hService = NativeMethods.OpenServiceW(scManager, serviceName,
                    NativeMethods.SERVICE_QUERY_CONFIG | NativeMethods.SERVICE_QUERY_STATUS);

                if (hService == IntPtr.Zero) continue;

                try
                {
                    if (!NativeMethods.QueryServiceStatus(hService, out var status))
                        continue;

                    var startType = GetServiceStartType(hService);
                    if (startType == uint.MaxValue) continue;

                    serviceStates[serviceName] = new ServiceState
                    {
                        StartType = startType,
                        WasRunning = status.dwCurrentState == NativeMethods.SERVICE_RUNNING
                    };
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(hService);
                }
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scManager);
        }

        snapshot.Set("services", serviceStates);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();
        int stopped = 0, failed = 0, skipped = 0;

        var servicesToThrottle = Settings.NormalizeServicesToThrottle(_settings.ServicesToThrottle);
        var scManager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scManager == IntPtr.Zero)
            return ApplyResult.Fail(Id, "Cannot open Service Control Manager (need admin)");

        try
        {
            foreach (var serviceName in servicesToThrottle)
            {
                var hService = NativeMethods.OpenServiceW(scManager, serviceName,
                    NativeMethods.SERVICE_QUERY_STATUS |
                    NativeMethods.SERVICE_STOP |
                    NativeMethods.SERVICE_CHANGE_CONFIG);
                if (hService == IntPtr.Zero) { skipped++; continue; }

                try
                {
                    if (!NativeMethods.QueryServiceStatus(hService, out var status))
                    { failed++; continue; }

                    if (status.dwCurrentState == NativeMethods.SERVICE_RUNNING)
                    {
                        if (NativeMethods.ControlService(hService, NativeMethods.SERVICE_CONTROL_STOP, out _))
                            stopped++;
                        else
                            failed++;
                    }
                    else
                    {
                        skipped++;
                    }

                    NativeMethods.ChangeServiceConfigW(hService,
                        NativeMethods.SERVICE_NO_CHANGE, NativeMethods.SERVICE_DEMAND_START,
                        NativeMethods.SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null);
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(hService);
                }
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scManager);
        }

        _servicesStopped = stopped;
        _isActive = stopped > 0;
        sw.Stop();

        return ApplyResult.Ok(Id,
            $"Stopped {stopped} services (skipped {skipped}, failed {failed})",
            optimized: stopped, failed: failed, skipped: skipped, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        var services = baseline.Get<Dictionary<string, ServiceState>>("services");
        if (services == null || services.Count == 0) { _isActive = false; return; }

        var scManager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.SC_MANAGER_CONNECT);
        if (scManager == IntPtr.Zero) return;

        try
        {
            foreach (var (serviceName, state) in services)
            {
                if (!IsAllowedService(serviceName) || !IsRestorableStartType(state.StartType))
                    continue;

                var hService = NativeMethods.OpenServiceW(scManager, serviceName,
                    NativeMethods.SERVICE_CHANGE_CONFIG |
                    NativeMethods.SERVICE_START);
                if (hService == IntPtr.Zero) continue;

                try
                {
                    NativeMethods.ChangeServiceConfigW(hService,
                        NativeMethods.SERVICE_NO_CHANGE, state.StartType,
                        NativeMethods.SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null);

                    if (state.WasRunning)
                        NativeMethods.StartServiceW(hService, 0, IntPtr.Zero);
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(hService);
                }
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(scManager);
        }

        _isActive = false;
        _servicesStopped = 0;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id, DisplayName = DisplayName, Category = Category,
        IsSupported = IsSupported, IsActive = _isActive,
        Summary = _isActive ? $"{_servicesStopped} services stopped" : "Inactive",
        Details = Settings.NormalizeServicesToThrottle(_settings.ServicesToThrottle).ToArray()
    };

    private static uint GetServiceStartType(IntPtr hService)
    {
        NativeMethods.QueryServiceConfigW(hService, IntPtr.Zero, 0, out var bytesNeeded);
        if (bytesNeeded == 0) return uint.MaxValue;

        var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
        try
        {
            if (!NativeMethods.QueryServiceConfigW(hService, buffer, bytesNeeded, out _))
                return uint.MaxValue;

            return (uint)Marshal.ReadInt32(buffer, 4);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() { }

    internal static bool IsAllowedService(string serviceName) =>
        Settings.NormalizeServicesToThrottle([serviceName]).Count > 0;

    internal static bool IsRestorableStartType(uint startType) =>
        startType is NativeMethods.SERVICE_AUTO_START
            or NativeMethods.SERVICE_DEMAND_START
            or NativeMethods.SERVICE_DISABLED;
}

public sealed class ServiceState
{
    public uint StartType { get; set; }
    public bool WasRunning { get; set; }
}
