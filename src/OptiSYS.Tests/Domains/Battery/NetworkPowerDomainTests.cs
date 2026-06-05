using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

/// <summary>
/// NetworkPowerDomain writes persistent HKLM adapter registry values, so its revert must be
/// verifiable: a failed restore write surfaces as false so the engine RETAINS the crash snapshot
/// (the only copy of the user's originals) instead of discarding it.
/// </summary>
public class NetworkPowerDomainTests
{
    private static DomainSnapshot Baseline(params (string sub, AdapterPowerState state)[] adapters)
    {
        var snapshot = new DomainSnapshot { DomainId = "network-power" };
        snapshot.Set("adapters", adapters.ToDictionary(a => a.sub, a => a.state));
        return snapshot;
    }

    private static AdapterPowerState State() => new()
    {
        DriverDesc = "Test Adapter",
        PnPCapabilities = 0x100,
        WakeOnMagicPacket = "1",
        WakeOnPattern = "1",
        EEE = "0",
    };

    [Fact]
    public void Revert_WhenAWriteFails_SignalsFailure()
    {
        var reg = new FakeRegistryWriter { FailAllWrites = true };
        var domain = new NetworkPowerDomain(reg);

        var reverted = domain.TryRevert(Baseline(("0001", State())));

        Assert.False(reverted);            // failure surfaced, not swallowed
    }

    [Fact]
    public void Revert_AllWritesSucceed_SignalsSuccess()
    {
        var reg = new FakeRegistryWriter();
        var domain = new NetworkPowerDomain(reg);

        var reverted = domain.TryRevert(Baseline(("0001", State())));

        Assert.True(reverted);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Revert_NoAdapterBaseline_IsCleanNoOp()
    {
        var reg = new FakeRegistryWriter { FailAllWrites = true };
        var domain = new NetworkPowerDomain(reg);

        // Nothing captured => nothing to restore => clean.
        Assert.True(domain.TryRevert(new DomainSnapshot { DomainId = "network-power" }));
    }

    private sealed class FakeRegistryWriter : IRegistryRestoreWriter
    {
        public bool FailAllWrites { get; set; }

        public bool SetDword(RegistryRoot root, string subKey, string name, int value) => !FailAllWrites;
        public bool SetString(RegistryRoot root, string subKey, string name, string value) => !FailAllWrites;
        public bool DeleteValue(RegistryRoot root, string subKey, string name) => !FailAllWrites;
    }
}
