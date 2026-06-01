using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

public class CpuParkingDomainTests
{
    private static readonly Guid Scheme = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Sub = NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP;
    private static readonly Guid Min = NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM;
    private static readonly Guid Max = NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM;
    private static readonly Guid Park = NativeMethods.GUID_PROCESSOR_PARKING_CORE_THRESHOLD;

    [Fact]
    public void Apply_WritesMinZero_AndMaxCap_OnBattery()
    {
        var power = new FakePowerScheme(Scheme);
        power.SetDc(Min, 5);
        power.SetDc(Max, 100);
        power.SetDc(Park, 50);
        var domain = new CpuParkingDomain(
            new Settings { CpuParkingMinProcessorDC = 0, CpuParkingMaxProcessorDC = 85 }, power);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);
        Assert.Equal(0u, power.GetDc(Min));    // minimum -> 0%
        Assert.Equal(85u, power.GetDc(Max));   // maximum -> 85% (the new ~15% reduction)
        Assert.True(domain.IsActive);
    }

    [Fact]
    public void Revert_RestoresCapturedMinAndMax()
    {
        var power = new FakePowerScheme(Scheme);
        power.SetDc(Min, 5);
        power.SetDc(Max, 100);
        power.SetDc(Park, 50);
        var domain = new CpuParkingDomain(
            new Settings { CpuParkingMinProcessorDC = 0, CpuParkingMaxProcessorDC = 85 }, power);

        var baseline = domain.CaptureBaseline();
        domain.Apply(baseline);
        domain.Revert(baseline);

        Assert.Equal(5u, power.GetDc(Min));    // restored
        Assert.Equal(100u, power.GetDc(Max));  // restored (previously the max was never restored)
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Apply_NoActiveScheme_FailsCleanly()
    {
        var power = new FakePowerScheme(Guid.Empty);
        var domain = new CpuParkingDomain(new Settings(), power);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.False(result.Success);
        Assert.False(domain.IsActive);
    }

    private sealed class FakePowerScheme : IPowerSchemeController
    {
        private readonly Guid _scheme;
        private readonly Dictionary<Guid, uint> _dc = [];

        public FakePowerScheme(Guid scheme) => _scheme = scheme;

        public void SetDc(Guid setting, uint value) => _dc[setting] = value;
        public uint GetDc(Guid setting) => _dc[setting];

        public Guid GetActiveScheme() => _scheme;

        public uint? ReadDcValue(Guid scheme, Guid subgroup, Guid setting) =>
            _dc.TryGetValue(setting, out var v) ? v : null;

        public bool WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value)
        {
            _dc[setting] = value;
            return true;
        }

        public void SetActiveScheme(Guid scheme) { }
    }
}
