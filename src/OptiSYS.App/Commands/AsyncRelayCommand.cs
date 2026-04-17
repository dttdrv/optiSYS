using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OptiSYS.Commands;

/// <summary>
/// Async-aware <see cref="ICommand"/>. Wraps a <c>Func&lt;Task&gt;</c> and exposes an
/// <see cref="IsExecuting"/> flag that toggles true while the task is in-flight; while
/// <see cref="IsExecuting"/> is true, <see cref="CanExecute"/> returns false so double-clicks
/// can't spawn overlapping runs.
///
/// <see cref="INotifyPropertyChanged"/> is implemented so a UI can bind to <c>IsExecuting</c>
/// (for e.g. spinners, busy overlays) without the ViewModel having to proxy the property.
/// </summary>
public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting == value) return;
            _isExecuting = value;
            OnPropertyChanged();
            // IsExecuting gates CanExecute, so any change must re-publish.
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) =>
        !IsExecuting && (_canExecute?.Invoke() ?? true);

    /// <summary>ICommand entry point — fire-and-forget from the XAML binder's perspective.</summary>
    public async void Execute(object? parameter) => await ExecuteAsync();

    /// <summary>Awaitable entry point for ViewModel-to-ViewModel composition or tests.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;

        IsExecuting = true;
        try
        {
            await _execute();
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
