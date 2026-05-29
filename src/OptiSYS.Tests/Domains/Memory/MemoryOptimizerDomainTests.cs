using Moq;
using OptiSYS.Core.Domains.Memory;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Memory;

public class MemoryOptimizerDomainTests
{
    [Fact]
    public void CaptureBaseline_UsesInjectedMemoryInfoService()
    {
        var settings = new Settings();
        var memoryInfo = new Mock<IMemoryInfoService>(MockBehavior.Strict);
        var optimizer = new Mock<IMemoryOptimizer>(MockBehavior.Strict);
        var snapshot = new MemoryInfo
        {
            TotalPhysicalBytes = 16L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 4L * 1024 * 1024 * 1024,
            CompressedBytes = 256L * 1024 * 1024,
        };

        memoryInfo.Setup(m => m.CurrentInfo).Returns((MemoryInfo?)null);
        memoryInfo.Setup(m => m.GetCurrentMemoryInfo()).Returns(snapshot);

        using var domain = new MemoryOptimizerDomain(settings, optimizer.Object, memoryInfo.Object);

        var baseline = domain.CaptureBaseline();

        Assert.Equal(MemoryOptimizerDomainId, baseline.DomainId);
        Assert.True(baseline.Has("totalBytes"));
        Assert.Equal(snapshot.TotalPhysicalBytes, baseline.Get<long>("totalBytes"));
        Assert.Equal(snapshot.AvailablePhysicalBytes, baseline.Get<long>("availableBytes"));
        Assert.Equal(snapshot.UsagePercent, baseline.Get<double>("usagePercent"));
        memoryInfo.Verify(m => m.GetCurrentMemoryInfo(), Times.Once);
    }

    [Fact]
    public void Apply_UsesInjectedSharedOptimizerAndSettings()
    {
        var settings = new Settings
        {
            AutoOptimizeMemoryEnabled = true,
            OptimizationLevel = OptimizationLevel.Aggressive,
            CacheMaxPercent = 12,
            MemoryThresholdPercent = 77,
            AccessedBitsDelayMs = 1500,
            EffectivenessTrackingEnabled = false,
            MemoryExcludedProcesses = ["alpha", "beta"],
        };

        var memoryInfo = new Mock<IMemoryInfoService>(MockBehavior.Strict);
        memoryInfo.Setup(m => m.CurrentInfo).Returns((MemoryInfo?)null);
        memoryInfo.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 16L * 1024 * 1024 * 1024,
            AvailablePhysicalBytes = 2L * 1024 * 1024 * 1024,
        });

        var optimizer = new Mock<IMemoryOptimizer>(MockBehavior.Strict);
        HashSet<string>? excluded = null;
        optimizer.SetupProperty(o => o.ExcludedProcesses);
        optimizer.SetupSet(o => o.ExcludedProcesses = It.IsAny<HashSet<string>>())
            .Callback<HashSet<string>>(value => excluded = value);
        optimizer.Setup(o => o.OptimizeAll(
                OptimizationLevel.Aggressive,
                12,
                77,
                false,
                1500,
                false,
                false))
            .Returns(new OptimizationResult
            {
                Success = true,
                Message = "optimized",
                ProcessesTrimmed = 4,
            });

        using var domain = new MemoryOptimizerDomain(settings, optimizer.Object, memoryInfo.Object);

        var result = domain.Apply(new DomainSnapshot { DomainId = MemoryOptimizerDomainId });

        Assert.True(result.Success);
        Assert.Equal(MemoryOptimizerDomainId, result.DomainId);
        Assert.Equal(4, result.ItemsOptimized);
        Assert.NotNull(excluded);
        Assert.Contains("alpha", excluded!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("beta", excluded!, StringComparer.OrdinalIgnoreCase);
        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive,
            12,
            77,
            false,
            1500,
            false,
            false), Times.Once);
    }

    [Fact]
    public void Dispose_DoesNotDisposeInjectedSharedDependencies()
    {
        var settings = new Settings();
        var memoryInfo = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        using var domain = new MemoryOptimizerDomain(settings, optimizer.Object, memoryInfo.Object);

        domain.Dispose();

        memoryInfo.Verify(m => m.Dispose(), Times.Never);
        optimizer.Verify(o => o.Dispose(), Times.Never);
    }

    private const string MemoryOptimizerDomainId = "memory-optimize";
}
