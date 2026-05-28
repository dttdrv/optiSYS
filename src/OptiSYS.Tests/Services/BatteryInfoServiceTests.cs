using System.Reflection;
using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class BatteryInfoServiceTests
{
    [Fact]
    public void Start_PollsImmediately_AndDoesNotReplaceExistingTimer()
    {
        var native = new Mock<INativeBridge>();
        var snapshot = new NativeBatteryInfo
        {
            PowerSource = PowerSource.Battery,
            HasBattery = true,
            ChargePercent = 61,
        };
        native.Setup(n => n.GetBatteryInfo(out snapshot)).Returns(true);

        using var service = new BatteryInfoService(native.Object);

        service.Start(60);
        var firstTimer = GetTimer(service);
        service.Start(1);
        var secondTimer = GetTimer(service);

        Assert.NotNull(firstTimer);
        Assert.Same(firstTimer, secondTimer);
        Assert.NotNull(service.CurrentInfo);
        Assert.Equal(61, service.CurrentInfo!.ChargePercent);
        native.Verify(n => n.GetBatteryInfo(out snapshot), Times.Once);
    }

    [Fact]
    public void Stop_ClearsTimer()
    {
        var native = new Mock<INativeBridge>();
        var empty = default(NativeBatteryInfo);
        native.Setup(n => n.GetBatteryInfo(out empty)).Returns(false);

        using var service = new BatteryInfoService(native.Object);
        service.Start(60);

        service.Stop();

        Assert.Null(GetTimer(service));
    }

    [Fact]
    public void Start_WhenNativeSnapshotFails_DoesNotOverwriteCurrentInfo()
    {
        var native = new Mock<INativeBridge>();
        var empty = default(NativeBatteryInfo);
        native.Setup(n => n.GetBatteryInfo(out empty)).Returns(false);

        using var service = new BatteryInfoService(native.Object);
        service.Start(60);

        Assert.Null(service.CurrentInfo);
    }

    private static object? GetTimer(BatteryInfoService service) =>
        typeof(BatteryInfoService)
            .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);
}
