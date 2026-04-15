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
        Assert.True(settings.AutoOptimizeMemoryEnabled == false);
        Assert.Equal(80, settings.MemoryThresholdPercent);
        Assert.Equal(2, settings.DebouncePowerChangeSeconds);
        Assert.Equal("System", settings.ThemeMode);
    }

    [Fact]
    public void Settings_ClampsOutOfRangeValues()
    {
        var settings = new Settings
        {
            DebouncePowerChangeSeconds = -1,
            MemoryThresholdPercent = 150,
            WindowWidth = 50
        };
        settings.Validate();

        Assert.Equal(1, settings.DebouncePowerChangeSeconds);
        Assert.Equal(95, settings.MemoryThresholdPercent);
        Assert.Equal(400, settings.WindowWidth);
    }

    [Fact]
    public void Settings_NullCollections_DefaultToEmpty()
    {
        var settings = new Settings
        {
            EcoQosExcludedProcesses = null!,
            MemoryExcludedProcesses = null!,
            ServicesToThrottle = null!
        };
        settings.Validate();

        Assert.NotNull(settings.EcoQosExcludedProcesses);
        Assert.NotNull(settings.MemoryExcludedProcesses);
        Assert.NotNull(settings.ServicesToThrottle);
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
