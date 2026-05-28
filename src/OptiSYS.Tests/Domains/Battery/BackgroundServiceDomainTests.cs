using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Native;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

public class BackgroundServiceDomainTests
{
    [Fact]
    public void GetStatus_ReportsOnlyCompiledAllowlistServices()
    {
        var settings = new Settings
        {
            ServicesToThrottle = ["WSearch", "Spooler", "BITS"]
        };

        using var domain = new BackgroundServiceDomain(settings);

        var status = domain.GetStatus();

        Assert.Equal(["WSearch", "BITS"], status.Details);
    }

    [Theory]
    [InlineData("WSearch", true)]
    [InlineData("wsearch", true)]
    [InlineData("Spooler", false)]
    [InlineData("", false)]
    public void IsAllowedService_UsesCompiledAllowlist(string serviceName, bool expected)
    {
        Assert.Equal(expected, BackgroundServiceDomain.IsAllowedService(serviceName));
    }

    [Theory]
    [InlineData(NativeMethods.SERVICE_AUTO_START, true)]
    [InlineData(NativeMethods.SERVICE_DEMAND_START, true)]
    [InlineData(NativeMethods.SERVICE_DISABLED, true)]
    [InlineData(0, false)]
    [InlineData(999, false)]
    public void IsRestorableStartType_RejectsInvalidSnapshotStartTypes(uint startType, bool expected)
    {
        Assert.Equal(expected, BackgroundServiceDomain.IsRestorableStartType(startType));
    }
}
