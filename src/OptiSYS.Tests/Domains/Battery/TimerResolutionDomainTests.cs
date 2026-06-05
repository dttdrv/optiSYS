using Moq;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

/// <summary>
/// Covers the bridge-required contract for the timer-resolution domain: the native bridge is a
/// mandatory dependency (no raw-P/Invoke fallback), and the ignore/clear writes route through it.
/// </summary>
public class TimerResolutionDomainTests
{
    [Fact]
    public void Ctor_NullBridge_Throws()
    {
        // The bridge is required: omitting it must fail loudly at construction rather than silently
        // falling through to real Win32 (which defeats mockability and hits the live system in tests).
        Assert.Throws<ArgumentNullException>(() => new TimerResolutionDomain(new Settings(), null!));
    }

    [Fact]
    public void Apply_Then_Revert_RoutesTimerWritesThroughBridge()
    {
        // Only assertable on Windows 11 22H2+ (the domain is observation-only on older builds).
        if (Environment.OSVersion.Version < new Version(10, 0, 22621))
            return;

        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(Environment.ProcessId);
        native.Setup(n => n.SetTimerResolution(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);

        var domain = new TimerResolutionDomain(new Settings(), native.Object);
        var baseline = domain.CaptureBaseline();
        domain.Apply(baseline);
        domain.Revert(baseline);

        // Every process the domain ignored on Apply must be cleared via the bridge on Revert.
        native.Verify(n => n.SetTimerResolution(true, It.IsAny<int>()), Times.AtLeastOnce);
        native.Verify(n => n.SetTimerResolution(false, It.IsAny<int>()), Times.AtLeastOnce);
    }
}
