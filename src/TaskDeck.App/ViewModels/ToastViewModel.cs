using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TaskDeck.App.ViewModels;

/// <summary>
/// 画面下端の取り消しトースト（S-08、UI 設計書 13.1）。6秒で消え、ホバー中は数えない。同時に1件。
/// </summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public const int DurationMs = 6000;

    private const int TickMs = 200;

    private int _token;
    private int _remainingMs;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAction))]
    private string? _actionLabel;

    [ObservableProperty]
    private string _iconKey = "IconInfo";

    /// <summary>マウスが乗っている間はカウントを止める。</summary>
    [ObservableProperty]
    private bool _isHovered;

    public bool HasAction => ActionLabel is not null;

    /// <summary>「元に戻す」が押された。</summary>
    public event EventHandler? ActionInvoked;

    /// <summary>トーストを出す（前のものは差し替える）。UI スレッドから呼ぶこと。</summary>
    public void Show(string message, string? actionLabel, string iconKey)
    {
        Message = message;
        ActionLabel = actionLabel;
        IconKey = iconKey;
        IsOpen = true;
        _remainingMs = DurationMs;
        CountDownAsync(++_token);
    }

    [RelayCommand]
    public void Close()
    {
        _token++;
        IsOpen = false;
    }

    [RelayCommand]
    private void InvokeAction()
    {
        Close();
        ActionInvoked?.Invoke(this, EventArgs.Empty);
    }

    private async void CountDownAsync(int token)
    {
        while (IsOpen && token == _token)
        {
            await Task.Delay(TickMs);
            if (token != _token)
            {
                return;
            }
            if (IsHovered)
            {
                continue;
            }
            _remainingMs -= TickMs;
            if (_remainingMs <= 0)
            {
                IsOpen = false;
                return;
            }
        }
    }
}
