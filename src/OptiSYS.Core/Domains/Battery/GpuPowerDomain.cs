using System.Diagnostics;
using Microsoft.Win32;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Sets Windows GPU preference to power-saving (integrated GPU).
/// Uses the documented UserGpuPreferences registry key that Windows
/// Settings > Display > Graphics uses under the hood.
/// Does NOT require vendor-specific APIs (NVIDIA/AMD).
/// </summary>
public sealed class GpuPowerDomain : IOptimizationDomain, IVerifiableRevert
{
    private readonly IRegistryRestoreWriter _registry;
    private bool _isActive;
    private bool? _hasDiscreteGpu;

    private const string GPU_PREFS_KEY = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GPU_GLOBAL_KEY = @"Software\Microsoft\DirectX\GraphicsSettings";

    public string Id => "gpu-power";
    public string DisplayName => "GPU Power Management";
    public string Category => "Battery";
    public bool IsSupported => _hasDiscreteGpu ??= DetectDiscreteGpuCore();
    public bool IsActive => _isActive;

    public GpuPowerDomain(IRegistryRestoreWriter? registry = null)
    {
        _registry = registry ?? new RegistryRestoreWriter();
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };

        try
        {
            using var globalKey = Registry.CurrentUser.OpenSubKey(GPU_GLOBAL_KEY);
            var globalPref = globalKey?.GetValue("DefaultGraphicsPreference") as int? ?? 0;
            snapshot.Set("globalPreference", globalPref);

            var appPrefs = new Dictionary<string, string>();
            using var prefsKey = Registry.CurrentUser.OpenSubKey(GPU_PREFS_KEY);
            if (prefsKey != null)
            {
                foreach (var valueName in prefsKey.GetValueNames())
                {
                    var value = prefsKey.GetValue(valueName) as string;
                    if (value != null)
                        appPrefs[valueName] = value;
                }
            }
            snapshot.Set("appPreferences", appPrefs);
        }
        catch { }

        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();

        if (_hasDiscreteGpu != true)
            return ApplyResult.Ok(Id, "No discrete GPU detected -- skipping", skipped: 1, duration: sw.Elapsed);

        try
        {
            using var globalKey = Registry.CurrentUser.CreateSubKey(GPU_GLOBAL_KEY);
            globalKey.SetValue("DefaultGraphicsPreference", 1, RegistryValueKind.DWord);

            _isActive = true;
            sw.Stop();
            return ApplyResult.Ok(Id, "Global GPU preference set to power saving", optimized: 1, duration: sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ApplyResult.Fail(Id, $"Failed to set GPU preference: {ex.Message}", sw.Elapsed);
        }
    }

    public void Revert(DomainSnapshot baseline) => TryRevert(baseline);

    /// <summary>
    /// Restore the captured global GPU preference. Returns false if the restore write fails — the
    /// engine / crash recovery must then RETAIN the snapshot so the only copy of the user's
    /// original preference is not lost. Original value 0 (the default) is restored by DELETING the
    /// value, matching how the value is absent on an untouched machine.
    /// </summary>
    public bool TryRevert(DomainSnapshot baseline)
    {
        var originalPref = baseline.Get<int>("globalPreference");

        bool ok = originalPref == 0
            ? _registry.DeleteValue(RegistryRoot.CurrentUser, GPU_GLOBAL_KEY, "DefaultGraphicsPreference")
            : _registry.SetDword(RegistryRoot.CurrentUser, GPU_GLOBAL_KEY, "DefaultGraphicsPreference", originalPref);

        _isActive = false;
        return ok;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive
            ? "GPU preference: power saving"
            : _hasDiscreteGpu == true ? "Inactive" : "No discrete GPU",
    };

    private bool DetectDiscreteGpuCore()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key == null) return false;

            int adapterCount = 0;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _)) continue;
                using var adapterKey = key.OpenSubKey(subKeyName);
                var desc = adapterKey?.GetValue("DriverDesc") as string ?? "";
                if (!string.IsNullOrEmpty(desc))
                    adapterCount++;
            }

            return adapterCount >= 2;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() { }
}
