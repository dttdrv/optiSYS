using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Reduces GPU power consumption on battery via power throttling.
/// Stub - to be ported from optiBAT.
/// </summary>
public sealed class GpuPowerDomain : IOptimizationDomain
{
    private bool _isActive;
    public string Id => "gpu-power";
    public string DisplayName => "GPU Power";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;
    public DomainSnapshot CaptureBaseline() => new() { DomainId = Id };
    public ApplyResult Apply(DomainSnapshot baseline) { _isActive = true; return ApplyResult.Ok(Id); }
    public void Revert(DomainSnapshot baseline) { _isActive = false; }
    public DomainStatus GetStatus() => new() { DomainId = Id, DisplayName = DisplayName, Category = Category, IsSupported = true, IsActive = _isActive };
    public void Dispose() { _isActive = false; }
}
