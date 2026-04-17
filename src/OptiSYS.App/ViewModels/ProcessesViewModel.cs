using System.Collections.ObjectModel;
using OptiSYS.Commands;
using OptiSYS.Core.Models;
using OptiSYS.Services;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Processes page. Enumerates running processes via
/// <see cref="IProcessEnumerator"/> (so the VM stays testable without touching
/// <c>System.Diagnostics.Process</c>) and exposes a search-filtered view plus a
/// Kill command gated on a selection.
///
/// <para>
/// <b>Why <see cref="FilteredProcesses"/> is a computed enumerable rather than a
/// second <see cref="ObservableCollection{T}"/>:</b> keeping it derived means it
/// cannot go out of sync with <see cref="Processes"/> or <see cref="SearchFilter"/>.
/// WinUI's ListView re-materializes its view when we raise PropertyChanged, which
/// is cheap at the scale this UI handles (hundreds of processes, not millions).
/// </para>
/// </summary>
public sealed class ProcessesViewModel : ViewModelBase
{
    private readonly IProcessEnumerator _processes;

    private ProcessMemoryInfo? _selectedProcess;
    private string _searchFilter = string.Empty;

    /// <summary>Raw snapshot of enumerated processes. Bind only for non-filtered scenarios.</summary>
    public ObservableCollection<ProcessMemoryInfo> Processes { get; } = new();

    public ProcessMemoryInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            // CanExecute of Kill depends on this being non-null; if the selection
            // changed, the bound Button must re-query CanExecute.
            if (SetField(ref _selectedProcess, value))
                KillSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            // Normalize null → "" so downstream filter code can rely on a non-null comparator.
            if (SetField(ref _searchFilter, value ?? string.Empty))
                OnPropertyChanged(nameof(FilteredProcesses));
        }
    }

    /// <summary>
    /// Case-insensitive substring filter over <see cref="ProcessMemoryInfo.ProcessName"/>.
    /// Empty/whitespace filter short-circuits to the full list so the common "no search
    /// active" path avoids an unnecessary lambda allocation.
    /// </summary>
    public IEnumerable<ProcessMemoryInfo> FilteredProcesses =>
        string.IsNullOrWhiteSpace(_searchFilter)
            ? Processes
            : Processes.Where(p =>
                p.ProcessName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase));

    public RelayCommand RefreshCommand      { get; }
    public RelayCommand KillSelectedCommand { get; }

    public ProcessesViewModel(IProcessEnumerator processes)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));

        RefreshCommand      = new RelayCommand(Refresh);
        KillSelectedCommand = new RelayCommand(KillSelected, () => _selectedProcess is not null);
    }

    private void Refresh()
    {
        // Wipe first, then repopulate — prevents stale PIDs from hanging around after the
        // underlying process list shrinks (e.g. after a kill).
        Processes.Clear();
        foreach (var p in _processes.EnumerateAll())
            Processes.Add(p);

        // FilteredProcesses reads Processes directly, so any change to the source list
        // needs an explicit PropertyChanged to kick WinUI bindings that watch the derived view.
        OnPropertyChanged(nameof(FilteredProcesses));
    }

    private void KillSelected()
    {
        // CanExecute already gates on non-null, but the command can still be invoked
        // programmatically — keep the defensive check.
        if (_selectedProcess is null) return;

        _processes.TryKill(_selectedProcess.ProcessId);
        // Refresh even if TryKill returned false: the list may have changed for unrelated
        // reasons, and a refresh is cheap enough to be the conservative choice.
        Refresh();
    }
}
