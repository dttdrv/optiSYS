using System.ComponentModel;
using Moq;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using OptiSYS.ViewModels;
using Xunit;

namespace OptiSYS.Tests.ViewModels;

/// <summary>
/// Behavior contract for <see cref="ProcessesViewModel"/>:
/// <list type="bullet">
///   <item>Refresh populates <c>Processes</c> from <see cref="IProcessEnumerator.EnumerateAll"/>,
///         clearing prior entries first so stale rows can't linger.</item>
///   <item><c>FilteredProcesses</c> is a derived view of <c>Processes</c> filtered by
///         <c>SearchFilter</c> (case-insensitive substring on <c>ProcessName</c>).</item>
///   <item>KillSelected is gated by a non-null <c>SelectedProcess</c>, delegates to
///         <see cref="IProcessEnumerator.TryKill"/>, then refreshes.</item>
///   <item>Null-arg guard.</item>
/// </list>
/// </summary>
public class ProcessesViewModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProcessMemoryInfo Proc(int pid, string name, long workingSetMb = 100) =>
        new()
        {
            ProcessId       = pid,
            ProcessName     = name,
            WorkingSetBytes = workingSetMb * 1024 * 1024,
        };

    private static (Mock<IProcessEnumerator> enumerator, ProcessesViewModel vm) CreateVm(
        IReadOnlyList<ProcessMemoryInfo>? initialList = null)
    {
        var enumerator = new Mock<IProcessEnumerator>();
        enumerator.Setup(e => e.EnumerateAll()).Returns(initialList ?? []);
        enumerator.Setup(e => e.TryKill(It.IsAny<int>())).Returns(true);
        var vm = new ProcessesViewModel(enumerator.Object);
        return (enumerator, vm);
    }

    // ── Null-arg guard ────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullEnumerator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessesViewModel(null!));
    }

    // ── Refresh ──────────────────────────────────────────────────────────────

    [Fact]
    public void RefreshCommand_PopulatesProcesses_FromEnumerator()
    {
        var data = new[] { Proc(100, "chrome"), Proc(200, "notepad") };
        var (enumerator, vm) = CreateVm(initialList: data);

        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.Processes.Count);
        Assert.Contains(vm.Processes, p => p.ProcessId == 100 && p.ProcessName == "chrome");
        Assert.Contains(vm.Processes, p => p.ProcessId == 200 && p.ProcessName == "notepad");
        enumerator.Verify(e => e.EnumerateAll(), Times.AtLeastOnce);
    }

    [Fact]
    public void RefreshCommand_SecondCall_ClearsAndRepopulates()
    {
        var first  = new[] { Proc(1, "old-a"), Proc(2, "old-b"), Proc(3, "old-c") };
        var second = new[] { Proc(9, "new-x") };
        var enumerator = new Mock<IProcessEnumerator>();
        enumerator.SetupSequence(e => e.EnumerateAll())
                  .Returns(first)
                  .Returns(second);
        var vm = new ProcessesViewModel(enumerator.Object);

        vm.RefreshCommand.Execute(null);  // 3 items
        vm.RefreshCommand.Execute(null);  // should shrink to 1

        Assert.Single(vm.Processes);
        Assert.Equal(9, vm.Processes[0].ProcessId);
    }

    [Fact]
    public void RefreshCommand_EmptyList_HandledWithoutError()
    {
        var (_, vm) = CreateVm(initialList: []);

        // Should not throw; Processes stays empty.
        vm.RefreshCommand.Execute(null);

        Assert.Empty(vm.Processes);
        Assert.Empty(vm.FilteredProcesses);
    }

    // ── SearchFilter + FilteredProcesses ──────────────────────────────────────

    [Fact]
    public void SearchFilter_Empty_IncludesAllProcesses()
    {
        var (_, vm) = CreateVm(initialList: [Proc(1, "a"), Proc(2, "b")]);
        vm.RefreshCommand.Execute(null);

        vm.SearchFilter = "";

        Assert.Equal(2, vm.FilteredProcesses.Count());
    }

    [Fact]
    public void SearchFilter_Substring_FiltersCaseInsensitively()
    {
        var data = new[] { Proc(1, "Chrome"), Proc(2, "chromium"), Proc(3, "notepad") };
        var (_, vm) = CreateVm(initialList: data);
        vm.RefreshCommand.Execute(null);

        vm.SearchFilter = "CHROM";   // upper-case query, mixed-case names

        var filtered = vm.FilteredProcesses.ToList();
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, p => Assert.Contains("chrom", p.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchFilter_Change_RaisesFilteredProcessesPropertyChanged()
    {
        var (_, vm) = CreateVm(initialList: [Proc(1, "a")]);
        vm.RefreshCommand.Execute(null);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SearchFilter = "a";

        Assert.Contains(nameof(vm.FilteredProcesses), changed);
        Assert.Contains(nameof(vm.SearchFilter), changed);
    }

    // ── KillSelectedCommand ───────────────────────────────────────────────────

    [Fact]
    public void KillSelectedCommand_WhenNothingSelected_CanExecuteIsFalse()
    {
        var (_, vm) = CreateVm();

        Assert.Null(vm.SelectedProcess);
        Assert.False(vm.KillSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void KillSelectedCommand_Execute_CallsTryKillWithSelectedPid()
    {
        var data = new[] { Proc(1234, "target-proc") };
        var (enumerator, vm) = CreateVm(initialList: data);
        vm.RefreshCommand.Execute(null);
        vm.SelectedProcess = vm.Processes.Single();

        vm.KillSelectedCommand.Execute(null);

        enumerator.Verify(e => e.TryKill(1234), Times.Once);
    }

    [Fact]
    public void KillSelectedCommand_Execute_RefreshesProcessesAfterKill()
    {
        // First enumeration: two entries. After the kill the service pretends the target is gone.
        var before = new[] { Proc(1234, "dying"), Proc(2, "survivor") };
        var after  = new[] { Proc(2, "survivor") };
        var enumerator = new Mock<IProcessEnumerator>();
        enumerator.SetupSequence(e => e.EnumerateAll())
                  .Returns(before)     // initial Refresh
                  .Returns(after);     // Refresh triggered by KillSelected
        enumerator.Setup(e => e.TryKill(It.IsAny<int>())).Returns(true);
        var vm = new ProcessesViewModel(enumerator.Object);

        vm.RefreshCommand.Execute(null);
        vm.SelectedProcess = vm.Processes.First(p => p.ProcessId == 1234);

        vm.KillSelectedCommand.Execute(null);

        // Enumerator was hit exactly twice: the initial refresh + the post-kill refresh.
        enumerator.Verify(e => e.EnumerateAll(), Times.Exactly(2));
        Assert.Single(vm.Processes);
        Assert.Equal(2, vm.Processes[0].ProcessId);
    }
}
