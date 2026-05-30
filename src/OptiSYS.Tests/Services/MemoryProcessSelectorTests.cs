using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class MemoryProcessSelectorTests
{
    private static NativeProcessInfo P(string name, long mib) =>
        new() { ProcessName = name, WorkingSetBytes = mib * 1024 * 1024 };

    private const long OneGiB = 1024L * 1024 * 1024;

    [Fact]
    public void SelectHeavy_KeepsOnlyOverThreshold_SortedDesc_CappedAtMax()
    {
        var procs = new[]
        {
            P("game", 3300),    // 3.22 GiB
            P("node", 2200),    // 2.15 GiB
            P("chrome", 1500),  // 1.46 GiB
            P("code", 1100),    // 1.07 GiB
            P("dwm", 1050),     // 1.025 GiB
            P("extra", 1030),   // 1.005 GiB  -> 6th over 1GiB, dropped by the cap of 5
            P("small", 400),    // 0.39 GiB   -> below threshold, excluded
        };

        var result = MemoryProcessSelector.SelectHeavy(procs, OneGiB, 5);

        Assert.Equal(5, result.Count);
        Assert.Equal(new[] { "game", "node", "chrome", "code", "dwm" }, result.Select(r => r.name).ToArray());
        Assert.DoesNotContain(result, r => r.name == "extra");   // dropped by cap
        Assert.DoesNotContain(result, r => r.name == "small");   // below 1 GiB
    }

    [Fact]
    public void SelectHeavy_NoneOverThreshold_ReturnsEmpty()
    {
        var procs = new[] { P("a", 200), P("b", 900) };
        Assert.Empty(MemoryProcessSelector.SelectHeavy(procs, OneGiB, 5));
    }
}
