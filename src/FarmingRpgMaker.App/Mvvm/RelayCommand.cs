using System.Windows.Input;

namespace FarmingRpgMaker.App.Mvvm;

/// <summary>Synchronous <see cref="ICommand"/> wrapping a delegate.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async <see cref="ICommand"/>: disabled while running, exceptions are routed to
/// <paramref name="onError"/> (or swallowed) so a failing handler never crashes the UI.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _isRunning;

    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync().ConfigureAwait(true);

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        _isRunning = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // UI commands must not take down the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A synchronous command with a typed parameter.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private readonly Action<T> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            _execute(value);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
