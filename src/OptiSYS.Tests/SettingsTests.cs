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
        Assert.True(settings.EcoQosEnabled);    // default-on: drain-aware targeting throttles only measured burners (audible exempt)
        Assert.False(settings.TimerResolutionEnabled);
        Assert.False(settings.BackgroundServicesEnabled);
        Assert.False(settings.UsbSuspendEnabled);
        Assert.False(settings.NetworkPowerEnabled);
        Assert.False(settings.GpuPowerEnabled);
        Assert.True(settings.CpuParkingEnabled);    // auto-on-battery (owner-approved battery rule relaxation): min 0% + max 85% + parking
        Assert.Equal(0, settings.CpuParkingMinProcessorDC);
        Assert.False(settings.DiskCoalescingEnabled);
        Assert.False(settings.WiFiOptimizerEnabled);    // OFF by default — net-degraded the connection on real hardware; opt-in only
        Assert.True(settings.WiFiDisableBackgroundScan);
        Assert.False(settings.WiFiStreamingMode);       // permanently off — never enabled
        Assert.True(settings.ServicesManualEnabled);    // AIO set; admin-gated, no-op unless elevated
        Assert.False(settings.HasCompletedOnboarding);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(50, settings.MemoryThresholdPercent);
        // Critical must sit ABOVE the reactive threshold so the reactive branch isn't dead code
        // (EvaluateMemoryPressureAsync tests >= critical first). 50/75, not the collapsed 75/75.
        Assert.Equal(75, settings.MemoryCriticalThresholdPercent);
        Assert.True(settings.MemoryCriticalThresholdPercent > settings.MemoryThresholdPercent);
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
            WindowWidth = 50,
            CommitRatioTrigger = 1.5
        };
        settings.Validate();

        Assert.Equal(1, settings.DebouncePowerChangeSeconds);
        Assert.Equal(95, settings.MemoryThresholdPercent);
        Assert.Equal(60, settings.MemoryCleanupDurationSeconds);
        Assert.Equal(5, settings.MemoryRepeatPasses);
        Assert.Equal(400, settings.WindowWidth);
        Assert.Equal(0.95, settings.CommitRatioTrigger);
    }

    [Fact]
    public void Settings_Validate_RepairsNonFiniteCommitRatioTrigger()
    {
        // A corrupted on-disk double (NaN survives JSON round-trips) must fall back to the
        // default rather than poisoning every predictor comparison.
        var settings = new Settings { CommitRatioTrigger = double.NaN };

        settings.Validate();

        Assert.Equal(0.65, settings.CommitRatioTrigger);
    }

    [Fact]
    public void Settings_Validate_RepairsCollapsedCriticalThreshold()
    {
        // A degenerate on-disk config (both at 75) must self-heal: critical lifts to threshold+10 so
        // the reactive branch is reachable again, not pinned dead behind the critical branch.
        var settings = new Settings
        {
            MemoryThresholdPercent = 75,
            MemoryCriticalThresholdPercent = 75,
        };

        settings.Validate();

        Assert.True(settings.MemoryCriticalThresholdPercent > settings.MemoryThresholdPercent);
        Assert.Equal(85, settings.MemoryCriticalThresholdPercent);
    }

    [Fact]
    public void Settings_Validate_CapsRepairedCriticalAt99()
    {
        // If the threshold is already very high, the +10 invariant caps at 99 (still > threshold).
        var settings = new Settings
        {
            MemoryThresholdPercent = 95,
            MemoryCriticalThresholdPercent = 90,   // below threshold after clamps → must repair
        };

        settings.Validate();

        Assert.True(settings.MemoryCriticalThresholdPercent > settings.MemoryThresholdPercent);
        Assert.Equal(99, settings.MemoryCriticalThresholdPercent);
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
