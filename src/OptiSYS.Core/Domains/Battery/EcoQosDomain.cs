using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Marks background processes with EcoQoS (efficiency mode).
/// Stub - to be ported from optiBAT.
/// </summary>
public sealed class EcoQosDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private bool _isActive;

    public string Id => "ecoqos";
    public string DisplayName => "EcoQoS";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public EcoQosDomain(Settings settings) { _settings = settings; }
    public DomainSnapshot CaptureBaseline() => new() { DomainId = Id };
    public ApplyResult Apply(DomainSnapshot baseline) { _isActive = true; return ApplyResult.Ok(Id); }
    public void Revert(DomainSnapshot baseline) { _isActive = false; }
    public DomainStatus GetStatus() => new() { DomainId = Id, DisplayName = DisplayName, Category = Category, IsSupported = true, IsActive = _isActive };
    public void Dispose() { _isActive = false; }
}
