using System.Windows.Input;

namespace ReDows.Gui.Navigation;

/// <summary>A minimal ICommand (no MVVM dependency): binds a button to a method, with an optional guard.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Re-query CanExecute (e.g. a long task started/finished) so bound buttons enable/disable.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
