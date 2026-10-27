using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace QuietIsland;

internal sealed class ActionCommand(Action action) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

internal sealed class AsyncActionCommand(Func<Task> action, Func<bool> canExecute) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public void Execute(object? parameter) => _ = ExecuteAsync();
    internal Task ExecuteAsync() => CanExecute(null) ? RunAsync() : Task.CompletedTask;
    private async Task RunAsync()
    {
        _running = true; Refresh();
        try { await action(); }
        catch (Exception exception) { AppLog.Write(exception); }
        finally { _running = false; Refresh(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
