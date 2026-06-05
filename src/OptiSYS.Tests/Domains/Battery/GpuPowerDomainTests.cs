using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

/// <summary>
/// GpuPowerDomain writes a persistent HKCU GPU-preference value, so its revert must be verifiable:
/// a failed restore write surfaces as false so the engine RETAINS the crash snapshot rather than
/// discarding the only copy of the user's original preference.
/// </summary>
public class GpuPowerDomainTests
{
    private static DomainSnapshot Baseline(int globalPref)
    {
        var snapshot = new DomainSnapshot { DomainId = "gpu-power" };
        snapshot.Set("globalPreference", globalPref);
        return snapshot;
    }

    [Fact]
    public void Revert_WhenAWriteFails_SignalsFailure()
    {
        var reg = new FakeRegistryWriter { FailAllWrites = true };
        var domain = new GpuPowerDomain(reg);

        var reverted = domain.TryRevert(Baseline(1));   // original was power-saving=1 -> needs a write

        Assert.False(reverted);
    }

    [Fact]
    public void Revert_AllWritesSucceed_SignalsSuccess()
    {
        var reg = new FakeRegistryWriter();
        var domain = new GpuPowerDomain(reg);

        var reverted = domain.TryRevert(Baseline(1));

        Assert.True(reverted);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Revert_OriginalWasZero_DeletesValue_AndFailureSurfaces()
    {
        // Original pref 0 (default) => revert DELETES the value; a failed delete must surface.
        var reg = new FakeRegistryWriter { FailAllWrites = true };
        var domain = new GpuPowerDomain(reg);

        Assert.False(domain.TryRevert(Baseline(0)));
    }

    private sealed class FakeRegistryWriter : IRegistryRestoreWriter
    {
        public bool FailAllWrites { get; set; }

        public bool SetDword(RegistryRoot root, string subKey, string name, int value) => !FailAllWrites;
        public bool SetString(RegistryRoot root, string subKey, string name, string value) => !FailAllWrites;
        public bool DeleteValue(RegistryRoot root, string subKey, string name) => !FailAllWrites;
    }
}
