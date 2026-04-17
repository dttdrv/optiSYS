using OptiSYS.Commands;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Dashboard page — a single screen showing live battery + memory stats.
///
/// <para>
/// Two data-flow styles are combined:
/// <list type="bullet">
///   <item><b>Push (battery):</b> <see cref="IBatteryInfoService"/> raises <c>Updated</c>
///         on each poll; we subscribe and lift the snapshot into <see cref="BatteryInfo"/>.</item>
///   <item><b>Pull (memory):</b> <see cref="IMemoryInfoService"/> has no internal timer.
///         We schedule one via <see cref="ITimerService"/> at 2s cadence and pull a fresh
///         snapshot on every tick.</item>
/// </list>
/// </para>
///
/// <para>
/// The timer subscription is stored and disposed in <see cref="Dispose"/> so navigation-away
/// doesn't leak a zombie tick that keeps this VM alive and hitting the OS every 2 seconds.
/// </para>
/// </summary>
public sealed class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly IBatteryInfoService _battery;
    private readonly IMemoryInfoService _memory;
    private readonly IDisposable _timerSubscription;

    private BatteryInfo? _batteryInfo;
    private MemoryInfo? _memoryInfo;
    private bool _isOptimizing;
    private string _statusMessage = "Ready";

    public BatteryInfo? BatteryInfo   { get => _batteryInfo;   set => SetField(ref _batteryInfo, value); }
    public MemoryInfo?  MemoryInfo    { get => _memoryInfo;    set => SetField(ref _memoryInfo, value); }
    public bool         IsOptimizing  { get => _isOptimizing;  set => SetField(ref _isOptimizing, value); }
    public string       StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

    /// <summary>
    /// Forces an immediate read of both battery and memory snapshots. Useful for the "Refresh"
    /// button on the Dashboard when the user wants a fresh reading between scheduled ticks.
    /// </summary>
    public RelayCommand RefreshCommand { get; }

    public DashboardViewModel(
        IBatteryInfoService battery,
        IMemoryInfoService memory,
        ITimerService timer)
    {
        _battery = battery ?? throw new ArgumentNullException(nameof(battery));
        _memory  = memory  ?? throw new ArgumentNullException(nameof(memory));
        ArgumentNullException.ThrowIfNull(timer);

        // Push: subscribe to battery service's own poll-event.
        _battery.Updated += OnBatteryUpdated;
        _battery.Start(5);

        // Pull: drive memory refresh ourselves. GetCurrentMemoryInfo is synchronous, fast,
        // and safe on the UI thread — no Task.Run wrapping needed.
        _timerSubscription = timer.Start(TimeSpan.FromSeconds(2), () =>
            MemoryInfo = _memory.GetCurrentMemoryInfo());

        RefreshCommand = new RelayCommand(RefreshNow);
    }

    private void OnBatteryUpdated(BatteryInfo info) => BatteryInfo = info;

    private void RefreshNow()
    {
        // Battery: prefer the latest cached snapshot over forcing another native call —
        // the service's internal 5s timer produces fresh data on its own schedule, and
        // piling on extra native reads serves no one.
        BatteryInfo = _battery.CurrentInfo;
        MemoryInfo  = _memory.GetCurrentMemoryInfo();
    }

    public void Dispose()
    {
        _battery.Updated -= OnBatteryUpdated;
        _timerSubscription.Dispose();
    }
}
