using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests;

public class SettingsTests
{
    [Fact]
    public void Settings_Defaults_AreValid()
    {
        var settings = new Settings();
        settings.Validate();

        Assert.True(settings.AutoOptimizeOnBattery);
        Assert.False(settings.AutomationPaused);
        Assert.True(settings.AutoOptimizeMemoryEnabled);
        Assert.False(settings.EcoQosEnabled);   // opt-in now (throttles all non-foreground processes)
        Assert.False(settings.TimerResolutionEnabled);
        Assert.False(settings.BackgroundServicesEnabled);
        Assert.False(settings.UsbSuspendEnabled);
        Assert.False(settings.NetworkPowerEnabled);
        Assert.False(settings.GpuPowerEnabled);
        Assert.True(settings.CpuParkingEnabled);    // enabled battery optimization: DC min processor state -> 0%
        Assert.False(settings.DiskCoalescingEnabled);
        Assert.True(settings.WiFiOptimizerEnabled);     // part of the all-in-one automatic optimization
        Assert.True(settings.ServicesManualEnabled);    // AIO set; admin-gated, no-op unless elevated
        Assert.False(settings.HasCompletedOnboarding);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(75, settings.MemoryThresholdPercent);
        Assert.Equal(5, settings.MemoryCheckIntervalSeconds);   // 5s reclaim cadence (matches RAMSpeed)
        Assert.Equal(30, settings.MemoryCooldownSeconds);
        Assert.Equal(OptimizationLevel.Balanced, settings.OptimizationLevel);
        Assert.Equal(2, settings.DebouncePowerChangeSeconds);
        Assert.Equal(15, settings.MemoryCleanupDurationSeconds);
        Assert.Equal(2, settings.MemoryRepeatPasses);
        Assert.Equal("System", settings.ThemeMode);
        Assert.Equal(1280, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
        Assert.Contains("explorer", settings.MemoryExcludedProcesses);
        Assert.Contains("ShellExperienceHost", settings.EcoQosExcludedProcesses);
        Assert.Contains("StartMenuExperienceHost", settings.TimerResolutionExcludedProcesses);
        Assert.Contains("Code", settings.ProtectedApplications);
        Assert.Contains("chrome", settings.ProtectedApplications);
    }

    [Fact]
    public void Settings_ClampsOutOfRangeValues()
    {
        var settings = new Settings
        {
            DebouncePowerChangeSeconds = -1,
            MemoryThresholdPercent = 150,
            MemoryCleanupDurationSeconds = 999,
            MemoryRepeatPasses = 99,
            WindowWidth = 50
        };
        settings.Validate();

        Assert.Equal(1, settings.DebouncePowerChangeSeconds);
        Assert.Equal(95, settings.MemoryThresholdPercent);
        Assert.Equal(60, settings.MemoryCleanupDurationSeconds);
        Assert.Equal(5, settings.MemoryRepeatPasses);
        Assert.Equal(400, settings.WindowWidth);
    }

    [Fact]
    public void Settings_NullCollections_DefaultToEmpty()
    {
        var settings = new Settings
        {
            EcoQosExcludedProcesses = null!,
            MemoryExcludedProcesses = null!,
            ProtectedApplications = null!,
            ServicesToThrottle = null!
        };
        settings.Validate();

        Assert.NotNull(settings.EcoQosExcludedProcesses);
        Assert.NotNull(settings.MemoryExcludedProcesses);
        Assert.NotNull(settings.ProtectedApplications);
        Assert.NotNull(settings.ServicesToThrottle);
        Assert.Contains("explorer", settings.MemoryExcludedProcesses);
        Assert.Contains("ShellExperienceHost", settings.EcoQosExcludedProcesses);
    }

    [Fact]
    public void Settings_Validate_RemovesServicesOutsideAllowlist()
    {
        var settings = new Settings
        {
            ServicesToThrottle = ["WSearch", "Spooler", "BITS", "LanmanServer"]
        };

        settings.Validate();

        Assert.Equal(["WSearch", "BITS"], settings.ServicesToThrottle);
    }

    [Fact]
    public void Settings_Validate_TrimsAndDeduplicatesServicesCaseInsensitive()
    {
        var settings = new Settings
        {
            ServicesToThrottle = [" WSearch ", "wsearch", "BITS", "bits"]
        };

        settings.Validate();

        Assert.Equal(["WSearch", "BITS"], settings.ServicesToThrottle);
    }
}

public class DomainStatusTests
{
    [Fact]
    public void ApplyResult_Ok_ReturnsSuccess()
    {
        var result = ApplyResult.Ok("test-id", "Operation succeeded");
        Assert.True(result.Success);
        Assert.Equal("test-id", result.DomainId);
        Assert.Equal("Operation succeeded", result.Message);
    }

    [Fact]
    public void ApplyResult_Fail_ReturnsFailure()
    {
        var result = ApplyResult.Fail("test-id", "Operation failed");
        Assert.False(result.Success);
        Assert.Equal("test-id", result.DomainId);
        Assert.Equal("Operation failed", result.Message);
    }
}

public class MemoryInfoTests
{
    [Fact]
    public void MemoryInfo_CalculatesUsagePercent()
    {
        var info = new MemoryInfo
        {
            TotalPhysicalBytes = 16L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.Equal(75.0, Math.Round(info.UsagePercent, 1));
        Assert.Equal(25.0, Math.Round(info.AvailablePercent, 1));
    }

    [Fact]
    public void MemoryInfo_FormatsBytes()
    {
        var info = new MemoryInfo
        {
            TotalPhysicalBytes = 16L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 4L * 1024 * 1024 * 1024,
        };

        Assert.Equal("16.0 GB", info.TotalDisplay);
        Assert.Equal("4.0 GB", info.AvailableDisplay);
    }
}

public class BatteryInfoTests
{
    [Fact]
    public void BatteryInfo_PowerSourceDisplay()
    {
        var info = new BatteryInfo { PowerSource = PowerSource.Ac };
        Assert.True(info.IsOnAc);
        Assert.False(info.IsOnBattery);
    }

    [Fact]
    public void BatteryInfo_TimeRemainingDisplay()
    {
        var info = new BatteryInfo { EstimatedTimeRemainingSeconds = 3661 };
        Assert.Equal("1h 1m", info.TimeRemainingDisplay);

        var info2 = new BatteryInfo { EstimatedTimeRemainingSeconds = 0 };
        Assert.Equal("N/A", info2.TimeRemainingDisplay);
    }
}
