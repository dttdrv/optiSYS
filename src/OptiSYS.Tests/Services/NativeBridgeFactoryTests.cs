using OptiSYS.Core.Native;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class NativeBridgeFactoryTests
{
    [Fact]
    public void Create_ReturnsWindowsNativeBridge()
    {
        using var bridge = NativeBridgeFactory.Create();

        Assert.IsType<WindowsNativeBridge>(bridge);
    }
}
