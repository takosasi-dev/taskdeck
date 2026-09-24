namespace TaskDeck.App.ViewModels;

/// <summary>
/// UI スレッドへ戻す手段。DataChangeHub や UndoService は UI スレッド以外から発火するので、
/// ViewModel はこれを通して自分の状態を変える。WPF の実装は Registration/ServiceRegistration.Shell.cs、テストでは即時実行の実装を渡す。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>UI スレッドで実行する（既に UI スレッドなら、そのまま後回しで積む）。</summary>
    void Post(Action action);
}

/// <summary>
/// 短い時間まとめてから1回だけ実行する（変更通知の束ね・検索の入力停止待ち）。
/// 重なった要求はいちばん遅い時刻に合わせる。UI スレッドから呼ぶこと。
/// 待ち時間は画面の都合なので IClock ではなく単調増加の時計（TickCount64）で測る（固定時計のテストで止まらないように）。
/// </summary>
public sealed class Debouncer(Func<Task> action)
{
    private long _dueAt;
    private bool _running;

    public void Schedule(int delayMs)
    {
        var at = Environment.TickCount64 + delayMs;
        if (at > _dueAt)
        {
            _dueAt = at;
        }
        if (_running)
        {
            return;
        }
        _running = true;
        RunAsync();
    }

    // 例外は Dispatcher の未処理例外ハンドラ（App.xaml.cs）まで上げてログに残す
    private async void RunAsync()
    {
        long wait;
        while ((wait = _dueAt - Environment.TickCount64) > 0)
        {
            await Task.Delay((int)wait);
        }
        _running = false;
        await action();
    }
}
