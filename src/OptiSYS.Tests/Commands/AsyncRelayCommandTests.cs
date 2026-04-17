using OptiSYS.Commands;
using Xunit;

namespace OptiSYS.Tests.Commands;

public class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_AwaitsTheProvidedDelegate()
    {
        var completed = false;
        var cmd = new AsyncRelayCommand(async () =>
        {
            await Task.Delay(10);
            completed = true;
        });

        await cmd.ExecuteAsync();

        Assert.True(completed);
    }

    [Fact]
    public async Task IsExecuting_TogglesTrueWhileRunningThenFalse()
    {
        var gate = new TaskCompletionSource();
        var cmd = new AsyncRelayCommand(() => gate.Task);

        // Before: idle.
        Assert.False(cmd.IsExecuting);

        var run = cmd.ExecuteAsync();

        // While the gate is closed, the command is in-flight.
        Assert.True(cmd.IsExecuting);

        gate.SetResult();
        await run;

        // After: idle again.
        Assert.False(cmd.IsExecuting);
    }

    [Fact]
    public async Task CanExecute_IsFalseWhileExecuting()
    {
        var gate = new TaskCompletionSource();
        var cmd = new AsyncRelayCommand(() => gate.Task);

        Assert.True(cmd.CanExecute(null));

        var run = cmd.ExecuteAsync();
        Assert.False(cmd.CanExecute(null));

        gate.SetResult();
        await run;
        Assert.True(cmd.CanExecute(null));
    }
}
