using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class MemoryInfoServiceTests
{
    [Fact]
    public void GetCurrentMemoryInfo_UsesNativeSnapshotWhenAvailable()
    {
        var native = new Mock<INativeBridge>();
        var snapshot = new NativeMemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 25,
            CommittedBytes = 60,
            StandbyCacheNormalPriorityBytes = 10,
            StandbyCacheReserveBytes = 5,
            ModifiedPageListBytes = 3,
        };
        native.Setup(n => n.GetMemoryInfo(out snapshot)).Returns(true);

        using var service = new MemoryInfoService(native.Object);
        var info = service.GetCurrentMemoryInfo();

        Assert.Equal(100, info.TotalPhysicalBytes);
        Assert.Equal(25, info.AvailablePhysicalBytes);
        Assert.Equal(60, info.CommittedBytes);
        Assert.Equal(15, info.StandbyCacheBytes);
        Assert.Equal(15, info.CachedBytes);
        Assert.Equal(3, info.ModifiedBytes);
        Assert.Same(info, service.CurrentInfo);
    }
}
