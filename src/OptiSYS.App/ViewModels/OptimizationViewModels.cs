using OptiSYS.Core.Models;

namespace OptiSYS.ViewModels;

public sealed class BatteryViewModel : ViewModelBase
{
    private BatteryInfo? _batteryInfo;
    private bool _isOnBattery;
    private bool _isOptimizing;

    public BatteryInfo? BatteryInfo { get => _batteryInfo; set => SetField(ref _batteryInfo, value); }
    public bool IsOnBattery { get => _isOnBattery; set => SetField(ref _isOnBattery, value); }
    public bool IsOptimizing { get => _isOptimizing; set => SetField(ref _isOptimizing, value); }
}

public sealed class MemoryViewModel : ViewModelBase
{
    private MemoryInfo? _memoryInfo;
    private PressureLevel _pressureLevel;
    private bool _isOptimizing;

    public MemoryInfo? MemoryInfo { get => _memoryInfo; set => SetField(ref _memoryInfo, value); }
    public PressureLevel PressureLevel { get => _pressureLevel; set => SetField(ref _pressureLevel, value); }
    public bool IsOptimizing { get => _isOptimizing; set => SetField(ref _isOptimizing, value); }
}

public sealed class ProcessesViewModel : ViewModelBase
{
    private List<ProcessMemoryInfo> _processes = [];

    public List<ProcessMemoryInfo> Processes { get => _processes; set => SetField(ref _processes, value); }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private Settings _settings = Settings.Load();

    public Settings Settings { get => _settings; set => SetField(ref _settings, value); }
}
