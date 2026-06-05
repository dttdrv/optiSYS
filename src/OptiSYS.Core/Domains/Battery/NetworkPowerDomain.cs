using System.Diagnostics;
using Microsoft.Win32;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Enables power management on network adapters.
/// Disables wake-on-LAN and enables power saving on Wi-Fi/Ethernet when on battery.
/// Uses registry-based approach for reliability across adapter types.
/// </summary>
public sealed class NetworkPowerDomain : IOptimizationDomain, IVerifiableRevert
{
    private readonly IRegistryRestoreWriter _registry;
    private bool _isActive;
    private int _adaptersModified;

    private const string NET_CLASS_KEY = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public string Id => "network-power";
    public string DisplayName => "Network Power Saving";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.NetworkPowerEnabled;

    public NetworkPowerDomain(IRegistryRestoreWriter? registry = null)
    {
        _registry = registry ?? new RegistryRestoreWriter();
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        var adapterStates = new Dictionary<string, AdapterPowerState>();

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NET_CLASS_KEY);
            if (classKey == null) { snapshot.Set("adapters", adapterStates); return snapshot; }

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;

                try
                {
                    using var adapterKey = classKey.OpenSubKey(subKeyName);
                    if (adapterKey == null) continue;

                    var driverDesc = adapterKey.GetValue("DriverDesc") as string;
                    if (string.IsNullOrEmpty(driverDesc)) continue;

                    var componentId = adapterKey.GetValue("ComponentId") as string ?? "";
                    if (componentId.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
                        componentId.Contains("vmware", StringComparison.OrdinalIgnoreCase) ||
                        componentId.Contains("vbox", StringComparison.OrdinalIgnoreCase))
                        continue;

                    adapterStates[subKeyName] = new AdapterPowerState
                    {
                        DriverDesc = driverDesc,
                        PnPCapabilities = adapterKey.GetValue("PnPCapabilities") as int? ?? 0,
                        WakeOnMagicPacket = adapterKey.GetValue("*WakeOnMagicPacket") as string ?? "1",
                        WakeOnPattern = adapterKey.GetValue("*WakeOnPattern") as string ?? "1",
                        EEE = adapterKey.GetValue("*EEE") as string,
                    };
                }
                catch { }
            }
        }
        catch { }

        snapshot.Set("adapters", adapterStates);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();
        int modified = 0, failed = 0, skipped = 0;

        try
        {
            var adapters = baseline.Get<Dictionary<string, AdapterPowerState>>("adapters");
            if (adapters == null || adapters.Count == 0)
                return ApplyResult.Fail(Id, "No adapter baseline captured", sw.Elapsed);

            foreach (var (subKeyName, _) in adapters)
            {
                try
                {
                    using var adapterKey = Registry.LocalMachine.OpenSubKey(
                        $@"{NET_CLASS_KEY}\{subKeyName}", writable: true);
                    if (adapterKey == null) { skipped++; continue; }

                    adapterKey.SetValue("PnPCapabilities", 0, RegistryValueKind.DWord);
                    adapterKey.SetValue("*WakeOnMagicPacket", "0", RegistryValueKind.String);
                    adapterKey.SetValue("*WakeOnPattern", "0", RegistryValueKind.String);

                    if (adapterKey.GetValue("*EEE") != null)
                        adapterKey.SetValue("*EEE", "1", RegistryValueKind.String);

                    modified++;
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail(Id, $"Registry error: {ex.Message}", sw.Elapsed);
        }

        _adaptersModified = modified;
        _isActive = modified > 0;
        sw.Stop();

        return ApplyResult.Ok(Id,
            $"Power saving enabled on {modified} adapters",
            modified, failed, skipped, sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline) => TryRevert(baseline);

    /// <summary>
    /// Restore each captured adapter's power values. Returns false if any restore write fails — the
    /// engine / crash recovery must then RETAIN the snapshot so the only copy of the user's
    /// originals is not lost. A missing adapter baseline is a clean no-op (nothing to restore).
    /// </summary>
    public bool TryRevert(DomainSnapshot baseline)
    {
        var adapters = baseline.Get<Dictionary<string, AdapterPowerState>>("adapters");
        if (adapters == null) { _isActive = false; return true; }

        bool ok = true;
        foreach (var (subKeyName, state) in adapters)
        {
            var subKey = $@"{NET_CLASS_KEY}\{subKeyName}";
            ok &= _registry.SetDword(RegistryRoot.LocalMachine, subKey, "PnPCapabilities", state.PnPCapabilities);
            ok &= _registry.SetString(RegistryRoot.LocalMachine, subKey, "*WakeOnMagicPacket", state.WakeOnMagicPacket);
            ok &= _registry.SetString(RegistryRoot.LocalMachine, subKey, "*WakeOnPattern", state.WakeOnPattern);

            if (state.EEE != null)
                ok &= _registry.SetString(RegistryRoot.LocalMachine, subKey, "*EEE", state.EEE);
        }

        _isActive = false;
        _adaptersModified = 0;
        return ok;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive ? $"{_adaptersModified} adapters optimized" : "Inactive",
    };

    public void Dispose() { }
}

/// <summary>
/// Tracks per-adapter registry state for power management revert.
/// </summary>
public sealed class AdapterPowerState
{
    public string DriverDesc { get; set; } = "";
    public int PnPCapabilities { get; set; }
    public string WakeOnMagicPacket { get; set; } = "1";
    public string WakeOnPattern { get; set; } = "1";
    public string? EEE { get; set; }
}
