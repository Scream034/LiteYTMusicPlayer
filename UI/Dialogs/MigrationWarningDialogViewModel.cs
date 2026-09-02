using System.Reactive;
using Avalonia.Threading;
using ReactiveUI;


namespace LMP.UI.Dialogs;

/// <summary>
/// ViewModel для диалога предупреждения об обновлении базы данных с таймером удержания.
/// </summary>
public sealed partial class MigrationWarningDialogViewModel : ViewModelBase
{
    private readonly Action _onClose;
    private readonly DispatcherTimer _timer;
    private int _secondsRemaining;

    [Reactive] public partial string Title { get; set; } = string.Empty;
    [Reactive] public partial string Message { get; set; } = string.Empty;
    [Reactive] public partial string ButtonText { get; set; } = string.Empty;
    [Reactive] public partial bool CanClose { get; set; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public MigrationWarningDialogViewModel(
        string title,
        string message,
        int countdownSeconds,
        Action onClose)
    {
        Title = title;
        Message = message;
        _onClose = onClose;
        _secondsRemaining = countdownSeconds;

        UpdateButtonState();

        CloseCommand = ReactiveCommand.Create(() =>
        {
            if (CanClose) _onClose();
        }, this.WhenAnyValue(x => x.CanClose));

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnTimerTick);
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            CanClose = true;
            UpdateButtonState();
        }
        else
        {
            UpdateButtonState();
        }
    }

    private void UpdateButtonState()
    {
        var L = LocalizationService.Instance;
        if (_secondsRemaining > 0)
        {
            ButtonText = string.Format(L["Dialog_LegacyPlaylists_CountdownButton"] ?? "Read ({0}s)", _secondsRemaining);
            CanClose = false;
        }
        else
        {
            ButtonText = L["Common_OK"] ?? "OK";
            CanClose = true;
        }
    }
}
