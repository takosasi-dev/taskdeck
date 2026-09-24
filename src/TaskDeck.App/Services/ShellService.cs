using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Settings;
using TaskDeck.App.Views;
using TaskDeck.App.Views.Focus;
using TaskDeck.App.Views.Palette;
using TaskDeck.App.Views.QuickInput;
using TaskDeck.App.Views.Shortcuts;
using TaskDeck.App.Views.Startup;
using TaskDeck.App.Views.Templates;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Services;

/// <summary>
/// 画面の出し入れの窓口。各画面を開くのは必ずここを通す（どこから開いても同じ規則になるように）。
/// - ダイアログ（設定・テンプレート・ショートカット一覧）は同時に1つだけ（要件 4.1.3）
/// - コマンドパレットは重ねてよい。フォーカスモード中は他を開かない
/// - トレイが無い間（波3まで）は、× と Esc の最後の段でアプリを終了／最小化する
/// 開く画面は DI から取り出す（各画面の担当が Registration/ServiceRegistration.*.cs に登録する）。
/// </summary>
public sealed class ShellService(IServiceProvider services, IClock clock, ILogger<ShellService> logger)
{
    private Window? _dialog;
    private Window? _focus;
    private Window? _firstRun;
    private bool _exiting;
    private bool _startupLogged;

    public MainWindow? MainWindow { get; private set; }

    /// <summary>トレイに常駐できる状態か（波3-H がトレイを作ったら true にする）。</summary>
    public bool IsTrayAvailable { get; set; }

    /// <summary>メイン画面の一覧で選ばれているタスク（メイン画面が更新する。テンプレート画面の「選択中のタスクから作る」が読む）。</summary>
    public IReadOnlyList<Guid> SelectedTaskIds { get; set; } = [];

    /// <summary>メイン画面の一覧がいま出している条件（検索・並び順を含む。メイン画面が更新する。CSV エクスポートが読む）。一覧以外を出しているときは null。</summary>
    public TaskQuery? CurrentListQuery { get; set; }

    private EventHandler<string>? _userNotice;
    private readonly List<string> _pendingNotices = [];

    /// <summary>
    /// 画面に短い知らせを出してほしいとき（未処理例外など）。メイン画面のトースト欄が購読する。
    /// 購読される前（トレイだけで動いていてメイン画面をまだ作っていない間）の知らせは溜めておき、購読されたときに1行にまとめて渡す。
    /// </summary>
    public event EventHandler<string>? UserNotice
    {
        add
        {
            _userNotice += value;
            if (_pendingNotices.Count > 0)
            {
                var message = string.Join("　", _pendingNotices);
                _pendingNotices.Clear();
                value?.Invoke(this, message);
            }
        }
        remove => _userNotice -= value;
    }

    /// <summary>UI スレッドから呼ぶ。</summary>
    public void NotifyUser(string message)
    {
        if (_userNotice is { } handler)
        {
            handler(this, message);
            return;
        }
        // ponytail: 溜めるのは新しい3件まで（トースト欄は1件ずつ差し替えるので、それより前は読まれない）
        if (_pendingNotices.Count == 3)
        {
            _pendingNotices.RemoveAt(0);
        }
        _pendingNotices.Add(message);
    }

    /// <summary>メイン画面にビューを出してほしいとき（パレット・トレイ・通知など）。メイン画面が購読して切り替える。</summary>
    public event EventHandler<ViewKey>? NavigateRequested;

    /// <summary>タスクを見せてほしいとき（パレットの検索結果・通知のクリックなど）。メイン画面が購読し、そのタスクが出るビューで選んで詳細ペインを開く。</summary>
    public event EventHandler<Guid>? RevealTaskRequested;

    /// <summary>メイン画面を前に出して、指定のビューに切り替える（UI スレッドで呼ぶ）。</summary>
    public void NavigateTo(ViewKey view)
    {
        ShowMainWindow();
        NavigateRequested?.Invoke(this, view);
    }

    /// <summary>メイン画面を前に出して、指定のタスクを選んだ状態にする（UI スレッドで呼ぶ）。</summary>
    public void RevealTask(Guid taskId)
    {
        ShowMainWindow();
        RevealTaskRequested?.Invoke(this, taskId);
    }

    /// <summary>窓を出さない起動か: --tray（自動起動）と -ToastActivated（止まっている間に通知を押された。中身は通知の担当が受ける）。</summary>
    public static bool IsBackgroundStart(IReadOnlyList<string> args) => args.Contains("--tray") || args.Contains("-ToastActivated");

    /// <summary>
    /// 起動時、DB の準備を待つ間にメイン画面を作っておく（まだ出さない。組み立ての時間を DB の準備と並べるため。NFR 3.2）。
    /// 窓を出さない起動では呼ばない（トレイだけで動いている間は、開くまで作らない）。
    /// </summary>
    public void PrepareMainWindow() => EnsureMainWindow();

    /// <summary>
    /// 起動時。窓を出さない起動は、トレイがあれば何も出さない（初回起動の3画面も次に窓を出す起動まで待つ）。
    /// firstRun なら初回起動の3画面（S-15）を出し、閉じたらメイン画面を出す。
    /// </summary>
    /// <returns>出した窓（初回起動の3画面かメイン画面）。何も出さなかったら null。</returns>
    public Window? StartUp(IReadOnlyList<string> args, bool firstRun = false)
    {
        if (IsBackgroundStart(args) && IsTrayAvailable)
        {
            return null;
        }
        if (firstRun)
        {
            var window = services.GetRequiredService<FirstRunWindow>();
            _firstRun = window;
            window.Closed += (_, _) =>
            {
                _firstRun = null;
                if (!_exiting)
                {
                    ShowMainWindow();
                }
            };
            Present(window);
            return window;
        }
        ShowMainWindow();
        return MainWindow;
    }

    /// <summary>2つ目の起動から合図が来たとき（UI スレッドで呼ぶ）。初回起動の3画面を出している間はそれを前に出す。</summary>
    public void HandleActivation(IReadOnlyList<string> args)
    {
        if (_firstRun is not null)
        {
            Present(_firstRun);
            return;
        }
        ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        var window = EnsureMainWindow();
        if (!window.IsVisible)
        {
            // 初めて出すときは、読み込み（クエリはスレッドプール）を先に始めてから出す（窓を出している間に読み進める）
            _ = window.BeginInitialLoad();
            window.ShowActivated = !QuietWindows;
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        if (QuietWindows)
        {
            return;
        }
        window.Activate();
        // 他アプリの裏に回っているときも確実に前へ出す
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private MainWindow EnsureMainWindow()
    {
        if (MainWindow is null)
        {
            MainWindow = services.GetRequiredService<MainWindow>();
            MainWindow.Closing += OnMainWindowClosing;
            MainWindow.ContentRendered += OnFirstRender;
            Application.Current.MainWindow = MainWindow;
        }
        return MainWindow;
    }

    /// <summary>メイン画面を引っ込める（Esc の最後の段）。トレイがあれば隠す、無ければ最小化。</summary>
    public void HideMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }
        if (IsTrayAvailable)
        {
            MainWindow.Hide();
        }
        else
        {
            MainWindow.WindowState = WindowState.Minimized;
        }
    }

    /// <summary>アプリの終了（トレイメニューの「終了」）。</summary>
    public void ExitApplication()
    {
        _exiting = true;
        if (MainWindow is not null)
        {
            MainWindow.Closing -= OnMainWindowClosing;
        }
        Application.Current.Shutdown();
    }

    public void OpenSettings(string? page = null)
    {
        var window = ShowDialog<SettingsWindow>();
        window?.NavigateTo(page);
    }

    public void OpenTemplates() => ShowDialog<TemplatesWindow>();

    public void OpenShortcuts() => ShowDialog<ShortcutsWindow>();

    public void OpenCommandPalette()
    {
        if (_focus is not null)
        {
            return;
        }
        var palette = services.GetRequiredService<CommandPaletteWindow>();
        palette.Owner = MainWindow is { IsVisible: true } ? MainWindow : null;
        Present(palette);
    }

    public void OpenFocusMode()
    {
        if (_focus is not null)
        {
            Present(_focus);
            return;
        }
        _dialog?.Close();
        _focus = services.GetRequiredService<FocusWindow>();
        _focus.Closed += (_, _) => _focus = null;
        Present(_focus);
    }

    public void OpenQuickInput() => Present(services.GetRequiredService<QuickInputWindow>());

    /// <summary>ダイアログではない常駐の小窓（使い捨てリストなど）を出す。確認用の起動では前面もフォーカスも奪わない。</summary>
    public void ShowToolWindow(Window window) => Present(window);

    /// <summary>
    /// 開発用フォルダで TASKDECK_DEV_NOACTIVATE=1 のときは、窓を出すだけで前面にもフォーカスにもしない。
    /// 確認作業で起動した窓が、依頼者が別のアプリで打っているキーを横取りしないようにするため。
    /// </summary>
    private bool QuietWindows => _quietWindows ??=
        services.GetService(typeof(AppPaths)) is AppPaths { IsDevelopment: true }
        && Environment.GetEnvironmentVariable("TASKDECK_DEV_NOACTIVATE") == "1";

    private bool? _quietWindows;

    private void Present(Window window)
    {
        // トレイだけで動いている間は、最初に作った窓（パレット・小窓など）を WPF が Application.MainWindow に入れてしまい、
        // WPF-UI がテーマの切り替えでその地を塗る（透明な縁が塗られる）。メイン画面（まだ無ければ null）に戻しておく
        if (Application.Current is { } app && app.MainWindow == window && window != MainWindow)
        {
            app.MainWindow = MainWindow;
        }
        window.ShowActivated = !QuietWindows;
        if (!window.IsVisible)
        {
            window.Show();
        }
        if (!QuietWindows)
        {
            window.Activate();
        }
    }

    /// <summary>ダイアログを1つだけ出す。既に別のダイアログがあれば、それを前に出して null（重ねない）。</summary>
    private T? ShowDialog<T>() where T : Window
    {
        if (_focus is not null)
        {
            return null;
        }
        if (_dialog is not null)
        {
            Present(_dialog);
            return _dialog as T;
        }
        var window = services.GetRequiredService<T>();
        if (MainWindow is { IsVisible: true })
        {
            window.Owner = MainWindow;
        }
        window.Closed += (_, _) => _dialog = null;
        _dialog = window;
        Present(window);
        return window;
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsTrayAvailable)
        {
            e.Cancel = true;
            MainWindow?.Hide();
            return;
        }
        Application.Current.Shutdown();
    }

    /// <summary>起動時間（プロセス開始 → 最初の描画）をログに出す（NFR 3.2）。</summary>
    private void OnFirstRender(object? sender, EventArgs e)
    {
        if (_startupLogged)
        {
            return;
        }
        _startupLogged = true;
        using var process = Process.GetCurrentProcess();
        var started = clock.ToUtc(process.StartTime);
        var elapsed = clock.UtcNow - started;
        logger.LogInformation("起動時間 {Milliseconds} ms（プロセス開始 → 最初の描画）", (int)elapsed.TotalMilliseconds);
    }
}
