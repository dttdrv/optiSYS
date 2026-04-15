using System.Diagnostics;
using Microsoft.Win32;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Enables USB selective suspend on idle devices.
/// Devices wake transparently when accessed.
/// </summary>
public sealed class UsbSuspendDomain : IOptimizationDomain
{
    private bool _isActive;
    private int _devicesModified;
    private const string USB_ENUM_KEY = @"SYSTEM\CurrentControlSet\Enum\USB";

    public string Id => "usb-suspend";
    public string DisplayName => "USB Selective Suspend";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        var deviceStates = new Dictionary<string, int>();

        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(USB_ENUM_KEY);
            if (usbKey == null) { snapshot.Set("devices", deviceStates); return snapshot; }

            foreach (var deviceId in usbKey.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = usbKey.OpenSubKey(deviceId);
                    if (deviceKey == null) continue;

                    foreach (var instanceId in deviceKey.GetSubKeyNames())
                    {
                        var paramPath = $@"{deviceId}\{instanceId}\Device Parameters";
                        using var paramKey = usbKey.OpenSubKey(paramPath);
                        if (paramKey == null) continue;

                        var value = paramKey.GetValue("SelectiveSuspendEnabled");
                        deviceStates[$"{deviceId}\\{instanceId}"] = value is int intVal ? intVal : -1;
                    }
                }
                catch { }
            }
        }
        catch { }

        snapshot.Set("devices", deviceStates);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();
        int modified = 0, failed = 0, skipped = 0;

        try
        {
            using var usbKey = Registry.LocalMachine.OpenSubKey(USB_ENUM_KEY);
            if (usbKey == null)
                return ApplyResult.Fail(Id, "Cannot access USB registry");

            foreach (var deviceId in usbKey.GetSubKeyNames())
            {
                try
                {
                    using var deviceKey = usbKey.OpenSubKey(deviceId);
                    if (deviceKey == null) continue;

                    foreach (var instanceId in deviceKey.GetSubKeyNames())
                    {
                        var paramPath = $@"{USB_ENUM_KEY}\{deviceId}\{instanceId}\Device Parameters";
                        using var paramKey = Registry.LocalMachine.OpenSubKey(paramPath, writable: true);

                        if (paramKey == null) { skipped++; continue; }

                        try
                        {
                            var current = paramKey.GetValue("SelectiveSuspendEnabled");
                            if (current is int val && val == 1) { skipped++; continue; }

                            paramKey.SetValue("SelectiveSuspendEnabled", 1, RegistryValueKind.DWord);
                            modified++;
                        }
                        catch { failed++; }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            return ApplyResult.Fail(Id, $"Registry access error: {ex.Message}");
        }

        _devicesModified = modified;
        _isActive = modified > 0;
        sw.Stop();

        return ApplyResult.Ok(Id, $"Enabled suspend on {modified} USB devices", modified, failed, skipped, sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        var devices = baseline.Get<Dictionary<string, int>>("devices");
        if (devices == null) { _isActive = false; return; }

        foreach (var (devicePath, originalValue) in devices)
        {
            try
            {
                var fullPath = $@"{USB_ENUM_KEY}\{devicePath}\Device Parameters";
                using var key = Registry.LocalMachine.OpenSubKey(fullPath, writable: true);
                if (key == null) continue;

                if (originalValue == -1)
                    key.DeleteValue("SelectiveSuspendEnabled", throwOnMissingValue: false);
                else
                    key.SetValue("SelectiveSuspendEnabled", originalValue, RegistryValueKind.DWord);
            }
            catch { }
        }

        _isActive = false;
        _devicesModified = 0;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id, DisplayName = DisplayName, Category = Category,
        IsSupported = IsSupported, IsActive = _isActive,
        Summary = _isActive ? $"{_devicesModified} devices set to suspend" : "Inactive",
    };

    public void Dispose() { }
}