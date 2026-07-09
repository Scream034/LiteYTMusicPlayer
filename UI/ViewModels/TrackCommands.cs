using System.Windows.Input;

namespace LMP.UI.ViewModels;

/// <summary>
/// Сверхлёгкая async команда для TrackItemViewModel.
/// Не создаёт Subject, Observable, Scheduler — только один int для флага выполнения.
/// Сравнение: ReactiveCommand ≈ 6-8 объектов, TrackAsyncCommand ≈ 1 объект.
/// </summary>
internal sealed class TrackAsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private int _isExecuting;

    /// <summary>
    /// Avalonia подписывается на CanExecuteChanged через WeakEvent (CommandCanExecuteChanged).
    /// Пустые аксессоры: CanExecute меняется только во время Execute (краткосрочно),
    /// Avalonia сама перепроверяет после завершения команды через CommandManager.
    /// Backing field не нужен — экономим аллокацию delegate-списка.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public TrackAsyncCommand(Func<Task> execute) => _execute = execute;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isExecuting) == 0;

    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Log.Error($"[TrackAsyncCommand] {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
        }
    }
}

/// <summary>
/// Сверхлёгкая sync команда для TrackItemViewModel.
/// CanExecute всегда true — никакого Observable overhead.
/// </summary>
internal sealed class TrackSyncCommand : ICommand
{
    private readonly Action _execute;

    public TrackSyncCommand(Action execute) => _execute = execute;

    /// <summary>
    /// CanExecute всегда true — событие никогда не стреляет.
    /// Пустые аксессоры предотвращают CS0067 и исключают аллокацию backing field.
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        try { _execute(); }
        catch (Exception ex) { Log.Error($"[TrackSyncCommand] {ex.Message}"); }
    }
}