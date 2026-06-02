using OptiSYS.Core.Domains.Network;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Network;

public class WiFiOptimizerDomainTests
{
    private static readonly Guid IfaceA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IfaceB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Apply_ConnectedInterface_DisablesBackgroundScan_NeverEnablesStreaming_KeepsHandleOpen()
    {
        var wlan = new FakeWlanInterop();
        wlan.AddInterface(IfaceA, connected: true, backgroundScan: true, streaming: false);
        var domain = NewDomain(wlan);

        var snapshot = domain.CaptureBaseline();
        var result = domain.Apply(snapshot);

        Assert.True(result.Success);
        Assert.True(domain.IsActive);
        Assert.False(wlan.Get(IfaceA, WlanOpcode.BackgroundScan));  // disabled
        Assert.False(wlan.Get(IfaceA, WlanOpcode.MediaStreaming));  // PERMANENTLY OFF — never enabled
        // The handle MUST stay open — closing it would revert the session-scoped opcodes.
        Assert.True(wlan.IsOpen);

        domain.Dispose();
    }

    [Fact]
    public void CaptureBaseline_RecordsCurrentValues_ForRevert()
    {
        var wlan = new FakeWlanInterop();
        wlan.AddInterface(IfaceA, connected: true, backgroundScan: true, streaming: false);
        var domain = NewDomain(wlan);

        var snapshot = domain.CaptureBaseline();
        domain.Apply(snapshot);
        domain.Revert(snapshot);

        Assert.False(domain.IsActive);
        Assert.True(wlan.Get(IfaceA, WlanOpcode.BackgroundScan));   // restored to captured true
        Assert.False(wlan.Get(IfaceA, WlanOpcode.MediaStreaming));  // restored to captured false
        Assert.False(wlan.IsOpen);                                  // handle closed on revert

        domain.Dispose();
    }

    [Fact]
    public void Apply_NoConnectedInterface_SkipsAndClosesHandle_NotActive()
    {
        var wlan = new FakeWlanInterop();
        wlan.AddInterface(IfaceA, connected: false, backgroundScan: true, streaming: false);
        var domain = NewDomain(wlan);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);            // a clean skip, not a failure
        Assert.Equal(1, result.ItemsSkipped);
        Assert.False(domain.IsActive);
        Assert.False(wlan.IsOpen);
        Assert.Empty(wlan.SetCalls);

        domain.Dispose();
    }

    [Fact]
    public void Apply_OnlyTouchesConnectedInterfaces()
    {
        var wlan = new FakeWlanInterop();
        wlan.AddInterface(IfaceA, connected: true, backgroundScan: true, streaming: false);
        wlan.AddInterface(IfaceB, connected: false, backgroundScan: true, streaming: false);
        var domain = NewDomain(wlan);

        domain.Apply(domain.CaptureBaseline());

        Assert.Contains(wlan.SetCalls, c => c.guid == IfaceA);
        Assert.DoesNotContain(wlan.SetCalls, c => c.guid == IfaceB);

        domain.Dispose();
    }

    [Fact]
    public void Apply_VerifyAfterWrite_DetectsDriverThatSilentlyIgnoresOpcode_WithoutFailing()
    {
        var wlan = new FakeWlanInterop();
        // Driver reports success but never changes the value (Intel/MediaTek behavior).
        wlan.AddInterface(IfaceA, connected: true, backgroundScan: true, streaming: false);
        wlan.IgnoreWrites.Add(IfaceA);
        var domain = NewDomain(wlan);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);                 // not a failure
        Assert.Equal(0, result.ItemsOptimized);
        Assert.Equal(1, result.ItemsSkipped);        // surfaced as "ignored by driver"
        Assert.Contains("ignored", result.Message, StringComparison.OrdinalIgnoreCase);

        domain.Dispose();
    }

    [Fact]
    public void Reapply_ReassertsSettings_AfterAReconnectResetThem()
    {
        var wlan = new FakeWlanInterop();
        wlan.AddInterface(IfaceA, connected: true, backgroundScan: true, streaming: false);
        var domain = NewDomain(wlan);

        domain.Apply(domain.CaptureBaseline());
        // Simulate a Wi-Fi reconnect resetting the opcodes to defaults.
        wlan.ForceSet(IfaceA, WlanOpcode.BackgroundScan, true);
        wlan.ForceSet(IfaceA, WlanOpcode.MediaStreaming, false);

        domain.Reapply();

        Assert.False(wlan.Get(IfaceA, WlanOpcode.BackgroundScan));   // re-asserted off
        Assert.False(wlan.Get(IfaceA, WlanOpcode.MediaStreaming));   // never enabled

        domain.Dispose();
    }

    [Fact]
    public void IsSupported_False_WhenWlanUnavailable()
    {
        var wlan = new FakeWlanInterop { Available = false };
        var domain = NewDomain(wlan);

        Assert.False(domain.IsSupported);

        domain.Dispose();
    }

    private static WiFiOptimizerDomain NewDomain(IWlanInterop wlan) =>
        new(new Settings { WiFiDisableBackgroundScan = true, WiFiStreamingMode = false }, wlan);

    private sealed class FakeWlanInterop : IWlanInterop
    {
        private readonly List<WlanInterfaceInfo> _interfaces = [];
        private readonly Dictionary<(Guid, WlanOpcode), bool> _values = [];

        public bool Available { get; set; } = true;
        public HashSet<Guid> IgnoreWrites { get; } = [];
        public List<(Guid guid, WlanOpcode opcode, bool value)> SetCalls { get; } = [];
        public bool IsOpen { get; private set; }

        public void AddInterface(Guid guid, bool connected, bool backgroundScan, bool streaming)
        {
            _interfaces.Add(new WlanInterfaceInfo(guid, connected, "Fake WLAN"));
            _values[(guid, WlanOpcode.BackgroundScan)] = backgroundScan;
            _values[(guid, WlanOpcode.MediaStreaming)] = streaming;
        }

        public bool Get(Guid guid, WlanOpcode opcode) => _values[(guid, opcode)];
        public void ForceSet(Guid guid, WlanOpcode opcode, bool value) => _values[(guid, opcode)] = value;

        public bool TryOpen()
        {
            if (!Available) return false;
            IsOpen = true;
            return true;
        }

        public IReadOnlyList<WlanInterfaceInfo> EnumerateInterfaces() => IsOpen ? _interfaces : [];

        public bool? QueryBool(Guid guid, WlanOpcode opcode) =>
            IsOpen && _values.TryGetValue((guid, opcode), out var v) ? v : null;

        public bool SetBool(Guid guid, WlanOpcode opcode, bool value)
        {
            if (!IsOpen) return false;
            SetCalls.Add((guid, opcode, value));
            if (!IgnoreWrites.Contains(guid))        // ignore-drivers report success but don't change
                _values[(guid, opcode)] = value;
            return true;
        }

        public void Close() => IsOpen = false;
        public void Dispose() => Close();
    }
}
