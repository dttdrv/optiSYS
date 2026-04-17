using OptiSYS.Commands;
using Xunit;

namespace OptiSYS.Tests.Commands;

public class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesAction()
    {
        var count = 0;
        var cmd = new RelayCommand(() => count++);

        cmd.Execute(null);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CanExecute_RespectsPredicate()
    {
        // No predicate supplied → CanExecute is unconditionally true.
        var cmdOpen = new RelayCommand(() => { });
        Assert.True(cmdOpen.CanExecute(null));

        // Predicate returns false → CanExecute is false and Execute becomes a guarded no-op.
        var invoked = false;
        var cmdGated = new RelayCommand(() => invoked = true, () => false);
        Assert.False(cmdGated.CanExecute(null));
        cmdGated.Execute(null);
        Assert.False(invoked);
    }

    [Fact]
    public void RaiseCanExecuteChanged_FiresEvent()
    {
        var cmd = new RelayCommand(() => { });
        var fired = 0;
        cmd.CanExecuteChanged += (_, _) => fired++;

        cmd.RaiseCanExecuteChanged();
        cmd.RaiseCanExecuteChanged();

        Assert.Equal(2, fired);
    }

    [Fact]
    public void GenericRelayCommand_PassesTypedParameter()
    {
        string? received = null;
        var cmd = new RelayCommand<string>(s => received = s);

        cmd.Execute("hello");

        Assert.Equal("hello", received);
    }
}
