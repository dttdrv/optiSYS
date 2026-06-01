using OptiSYS.Lab.Probes;
using Xunit;

namespace OptiSYS.Tests.Lab;

public class PowercfgProcessorParserTests
{
    private const string MaxStateSample = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
          Subgroup GUID: 54533251-82be-4824-96c1-47b60b740d00  (Processor power management)
            Power Setting GUID: bc5038f7-23e0-4960-96da-33abaf5935ec  (Maximum processor state)
              Current AC Power Setting Index: 0x00000064
              Current DC Power Setting Index: 0x00000055
        """;

    [Fact]
    public void ParseAcDcIndex_ReadsHexValues()
    {
        var (ac, dc) = PowercfgProcessorParser.ParseAcDcIndex(MaxStateSample);
        Assert.Equal(100, ac);   // 0x64
        Assert.Equal(85, dc);    // 0x55 — the cap optiSYS writes
    }

    [Fact]
    public void ParseAcDcIndex_HandlesDecimal()
    {
        var (ac, dc) = PowercfgProcessorParser.ParseAcDcIndex(
            "Current AC Power Setting Index: 100\nCurrent DC Power Setting Index: 5");
        Assert.Equal(100, ac);
        Assert.Equal(5, dc);
    }

    [Fact]
    public void ParseAcDcIndex_MissingOrEmpty_ReturnsNulls()
    {
        Assert.Equal((null, null), PowercfgProcessorParser.ParseAcDcIndex(""));
        Assert.Equal((null, null), PowercfgProcessorParser.ParseAcDcIndex("no indices here"));
    }
}
