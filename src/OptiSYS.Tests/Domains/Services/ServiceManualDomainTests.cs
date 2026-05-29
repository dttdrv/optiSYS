using OptiSYS.Core.Domains.Services;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Services;

public class ServiceManualDomainTests
{
    private const uint Auto = 2, Manual = 3, Disabled = 4;

    [Fact]
    public void Apply_FlipsAutomaticServicesToManual_LeavesOthersAlone()
    {
        var store = new FakeServiceConfigStore();
        // A curated target that is Automatic should be flipped; one already Manual must be left.
        store.Set("MapsBroker", Auto);
        store.Set("Fax", Manual);
        var domain = NewDomain(store, elevated: true);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);
        Assert.True(domain.IsActive);
        Assert.Equal(Manual, store.Get("MapsBroker"));   // Auto → Manual
        Assert.Equal(Manual, store.Get("Fax"));          // already Manual, untouched
        Assert.Equal(1, result.ItemsOptimized);
        Assert.DoesNotContain(store.WriteLog, w => w.name == "Fax"); // never written
    }

    [Fact]
    public void Apply_NeverStopsAService_OnlyChangesStartType()
    {
        var store = new FakeServiceConfigStore();
        store.Set("MapsBroker", Auto);
        var domain = NewDomain(store, elevated: true);

        domain.Apply(domain.CaptureBaseline());

        // The fake has no "stop" concept — assert only start-type writes happened, never DEMAND→stop.
        Assert.All(store.WriteLog, w => Assert.True(w.value is Manual or Auto or Disabled));
    }

    [Fact]
    public void Apply_WhenNotElevated_IsACleanSkip()
    {
        var store = new FakeServiceConfigStore();
        store.Set("MapsBroker", Auto);
        var domain = NewDomain(store, elevated: false);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);             // not a failure
        Assert.False(domain.IsActive);
        Assert.Empty(store.WriteLog);            // nothing changed
        Assert.Equal(Auto, store.Get("MapsBroker"));
    }

    [Fact]
    public void Revert_RestoresOriginalStartTypes()
    {
        var store = new FakeServiceConfigStore();
        store.Set("MapsBroker", Auto);
        var domain = NewDomain(store, elevated: true);

        var snapshot = domain.CaptureBaseline();
        domain.Apply(snapshot);
        Assert.Equal(Manual, store.Get("MapsBroker"));

        domain.Revert(snapshot);

        Assert.Equal(Auto, store.Get("MapsBroker"));   // restored exactly
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Apply_NeverTouchesAHardBlockedService_EvenIfAutomatic()
    {
        // WlanSvc is hard-blocked (our Wi-Fi domain needs it running). Even though it's not in the
        // target set, prove the block-list guard by checking it is never written.
        var store = new FakeServiceConfigStore();
        store.Set("WlanSvc", Auto);
        foreach (var name in Settings.ServicesToSetManual)
            store.Set(name, Auto);
        var domain = NewDomain(store, elevated: true);

        domain.Apply(domain.CaptureBaseline());

        Assert.Equal(Auto, store.Get("WlanSvc"));                         // untouched
        Assert.DoesNotContain(store.WriteLog, w => w.name == "WlanSvc");
        // And no hard-blocked service is ever written.
        Assert.All(store.WriteLog, w => Assert.DoesNotContain(w.name, Settings.ServicesNeverManual));
    }

    [Fact]
    public void IsSupported_ReflectsElevation()
    {
        Assert.False(NewDomain(new FakeServiceConfigStore(), elevated: false).IsSupported);
        Assert.True(NewDomain(new FakeServiceConfigStore(), elevated: true).IsSupported);
    }

    private static ServiceManualDomain NewDomain(IServiceConfigStore store, bool elevated) =>
        new(store, () => elevated);

    private sealed class FakeServiceConfigStore : IServiceConfigStore
    {
        private readonly Dictionary<string, uint> _startTypes = new(StringComparer.OrdinalIgnoreCase);
        public List<(string name, uint value)> WriteLog { get; } = [];

        public void Set(string name, uint startType) => _startTypes[name] = startType;
        public uint Get(string name) => _startTypes[name];

        public uint? GetStartType(string serviceName) =>
            _startTypes.TryGetValue(serviceName, out var v) ? v : null;

        public bool SetStartType(string serviceName, uint startType)
        {
            if (!_startTypes.ContainsKey(serviceName)) return false;
            _startTypes[serviceName] = startType;
            WriteLog.Add((serviceName, startType));
            return true;
        }
    }
}
