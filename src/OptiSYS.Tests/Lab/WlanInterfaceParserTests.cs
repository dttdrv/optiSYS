using OptiSYS.Lab.Probes;
using Xunit;

namespace OptiSYS.Tests.Lab;

public class WlanInterfaceParserTests
{
    // Captured from a real `netsh wlan show interfaces` run (two-adapter DBS Qualcomm card).
    private const string TwoInterfaceSample = """
        There are 2 interfaces on the system:

            Name                   : WiFi
            Description            : Qualcomm WCN685x Wi-Fi 6E DBS WiFiCx Network Adapter
            GUID                   : 375fc936-64f0-4c50-8d39-9a10ff554930
            State                  : connected
            SSID                   : VIVACOM_B798_5G
            Band                   : 5 GHz
            Channel                : 36
            Radio type             : 802.11ac
            Signal                 : 100%
            Rssi                   : -40
            Receive rate (Mbps)    : 173.3
            Transmit rate (Mbps)   : 173.3

            Name                   : WiFi 3
            Description            : Qualcomm WCN685x Wi-Fi 6E DBS WiFiCx Network Adapter #3
            State                  : disconnected
        """;

    [Fact]
    public void Parse_TwoInterfaces_ExtractsBothBlocks()
    {
        var result = WlanInterfaceParser.Parse(TwoInterfaceSample);

        Assert.Equal(2, result.Count);
        Assert.Equal("WiFi", result[0].Name);
        Assert.Equal("WiFi 3", result[1].Name);
    }

    [Fact]
    public void Parse_ConnectedInterface_ReadsKeyFields()
    {
        var iface = WlanInterfaceParser.Parse(TwoInterfaceSample)[0];

        Assert.Equal("connected", iface.State);
        Assert.Equal("VIVACOM_B798_5G", iface.Ssid);
        Assert.Equal("5 GHz", iface.Band);
        Assert.Equal("36", iface.Channel);
        Assert.Equal("802.11ac", iface.RadioType);
        Assert.Equal("100%", iface.SignalPercent);
        Assert.Equal("-40", iface.Rssi);
        Assert.Equal("173.3", iface.ReceiveRateMbps);
        Assert.Equal("173.3", iface.TransmitRateMbps);
    }

    [Fact]
    public void Parse_DisconnectedInterface_HasEmptyConnectionFields()
    {
        var iface = WlanInterfaceParser.Parse(TwoInterfaceSample)[1];

        Assert.Equal("disconnected", iface.State);
        Assert.Equal("", iface.Ssid);
        Assert.Equal("", iface.Channel);
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(WlanInterfaceParser.Parse(""));
        Assert.Empty(WlanInterfaceParser.Parse("   \r\n  "));
    }

    [Fact]
    public void Parse_NoWlanInterfaces_ReturnsEmpty()
    {
        // netsh prints this when the WLAN service is running but no adapter is present.
        Assert.Empty(WlanInterfaceParser.Parse("There is no wireless interface on the system."));
    }
}
