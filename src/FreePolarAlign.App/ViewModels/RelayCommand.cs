using System.Windows.Input;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// An <see cref="ICommand"/> over an async delegate, with re-entrancy guarded
/// so a slow-clicking user cannot send two <see cref="FreePolarAlign.Core.Engine.EngineCommand"/>s
/// concurrently through the same button -- the engine's own command gate
/// (<c>AlignmentSession._commandGate</c>) would serialise them anyway, but
/// disabling the button while a command is in flight is the honest UI
/// reflection of that, not just a cosmetic nicety.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
