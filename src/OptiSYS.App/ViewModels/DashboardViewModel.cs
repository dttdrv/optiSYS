using OptiSYS.Core.Models;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Dashboard page showing live system metrics.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private BatteryInfo? _batteryInfo;
    private MemoryInfo? _memoryInfo;
    private bool _isOptimizing;
    private string _statusMessage = "Ready";

    public BatteryInfo? BatteryInfo { get => _batteryInfo; set => SetField(ref _batteryInfo, value); }
    public MemoryInfo? MemoryInfo { get => _memoryInfo; set => SetField(ref _memoryInfo, value); }
    public bool IsOptimizing { get => _isOptimizing; set => SetField(ref _isOptimizing, value); }
    public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }
}
