using OptiSYS.Commands;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Memory page. Auto-polls memory stats every 2s via
/// <see cref="ITimerService"/> and exposes an async Optimize command that routes
/// through <see cref="IMemoryOptimizer.TrimProcessWorkingSets"/> off the UI thread.
///
/// <para>
/// <b>Why a derived <see cref="PressureLevel"/>:</b> keeping it as a computed
/// projection of <c>MemoryInfo.UsagePercent</c> guarantees the pair can never drift.
/// A settable pair would let partial updates leave them inconsistent (e.g. 95%
/// usage but Level=Normal). The setter of <see cref="MemoryInfo"/> raises
/// PropertyChanged for both so WinUI bindings re-query.
/// </para>
/// </summary>
public sealed class MemoryViewModel : ViewModelBase, IDisposable
{
    // Pressure thresholds mirror the contract in the plan §M3.C:
    //   <60 Normal, [60,75) Elevated, [75,90) High, ≥90 Critical.
    // Kept as named consts so a policy tweak is a single-line change.
    private const double ElevatedThreshold = 60.0;
    private const double HighThreshold     = 75.0;
    private const double CriticalThreshold = 90.0;

    private readonly IMemoryInfoService _memory;
    private readonly IMemoryOptimizer   _optimizer;
    private readonly IDisposable        _timerSubscription;

    private MemoryInfo? _memoryInfo;
    private bool        _isOptimizing;
    private string?     _lastOptimizationResult;

    public MemoryInfo? MemoryInfo
    {
        get => _memoryInfo;
        // PressureLevel is derived from UsagePercent, so any MemoryInfo change
        // must fire PropertyChanged for the computed property too — otherwise
        // a XAML binding on PressureLevel would stay stale.
        set
        {
            if (SetField(ref _memoryInfo, value))
                OnPropertyChanged(nameof(PressureLevel));
        }
    }

    public PressureLevel PressureLevel
    {
        get
        {
            var pct = _memoryInfo?.UsagePercent ?? 0;
            if (pct >= CriticalThreshold) return PressureLevel.Critical;
            if (pct >= HighThreshold)     return PressureLevel.High;
            if (pct >= ElevatedThreshold) return PressureLevel.Elevated;
            return PressureLevel.Normal;
        }
    }

    public bool IsOptimizing
    {
        get => _isOptimizing;
        set => SetField(ref _isOptimizing, value);
    }

    public string? LastOptimizationResult
    {
        get => _lastOptimizationResult;
        set => SetField(ref _lastOptimizationResult, value);
    }

    public AsyncRelayCommand OptimizeCommand { get; }
    public RelayCommand      RefreshCommand  { get; }

    public MemoryViewModel(
        IMemoryInfoService memoryInfo,
        IMemoryOptimizer   optimizer,
        ITimerService      timer)
    {
        _memory    = memoryInfo ?? throw new ArgumentNullException(nameof(memoryInfo));
        _optimizer = optimizer  ?? throw new ArgumentNullException(nameof(optimizer));
        ArgumentNullException.ThrowIfNull(timer);

        // 2-second cadence matches Dashboard's memory poll. GetCurrentMemoryInfo()
        // is a cheap synchronous snapshot (cached internally); no Task.Run needed.
        _timerSubscription = timer.Start(TimeSpan.FromSeconds(2), PollMemory);

        OptimizeCommand = new AsyncRelayCommand(OptimizeAsync);
        RefreshCommand  = new RelayCommand(PollMemory);
    }

    private void PollMemory() => MemoryInfo = _memory.GetCurrentMemoryInfo();

    /// <summary>
    /// Runs the optimizer off the UI thread and reports the outcome.
    /// <para>
    /// <see cref="IsOptimizing"/> is flipped <b>synchronously</b> before the await
    /// so XAML bindings see the in-flight state the moment the command fires. The
    /// try/finally guarantees it gets cleared even if the optimizer throws, so a
    /// partial failure can't leave the UI stuck in "optimizing…" forever.
    /// </para>
    /// </summary>
    private async Task OptimizeAsync()
    {
        IsOptimizing = true;
        try
        {
            // TrimProcessWorkingSets calls EmptyWorkingSet over every accessible
            // process — bursty native IO. Hopping onto the thread pool keeps the
            // UI thread responsive during the trim.
            var (trimmed, failed, skipped, earlyExit) =
                await Task.Run(() => _optimizer.TrimProcessWorkingSets());

            LastOptimizationResult = earlyExit
                ? $"Stopped early — trimmed {trimmed}, failed {failed}, skipped {skipped}"
                : $"Trimmed {trimmed} process(es); {failed} failed, {skipped} skipped";

            // Pull a fresh snapshot so UsagePercent (and PressureLevel) reflect
            // the post-trim state rather than the pre-trim reading.
            PollMemory();
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    public void Dispose() => _timerSubscription.Dispose();
}
