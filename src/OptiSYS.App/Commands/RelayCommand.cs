using System.Windows.Input;

namespace OptiSYS.Commands;

/// <summary>
/// Minimal synchronous <see cref="ICommand"/> that delegates Execute to an <see cref="Action"/>
/// and (optionally) CanExecute to a <see cref="Func{TResult}"/>. When no predicate is supplied
/// the command is always executable.
///
/// WinUI 3 binds <c>Button.Command</c> to <see cref="System.Windows.Input.ICommand"/> even
/// though that type historically lived in WPF — it's in the BCL's <c>System.ObjectModel</c>
/// assembly and XAML binding works by interface, not by CLR identity.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _execute();
    }

    /// <summary>
    /// Notify any listeners (typically the XAML binding engine) that <see cref="CanExecute"/>
    /// may now return a different value — bound controls will re-query and enable/disable
    /// themselves accordingly.
    /// </summary>
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Strongly-typed variant of <see cref="RelayCommand"/>. The <typeparamref name="T"/> parameter
/// is what XAML will pass via <c>CommandParameter="…"</c>; values that can't be cast are treated
/// as the default of <typeparamref name="T"/>.
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke(Cast(parameter)) ?? true;

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _execute(Cast(parameter));
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? Cast(object? parameter) =>
        parameter is T t ? t : default;
}
