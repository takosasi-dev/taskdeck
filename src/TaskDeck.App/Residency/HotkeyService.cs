using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Residency;

/// <summary>グローバルホットキーで呼ぶもの（値は RegisterHotKey の id）。</summary>
public enum HotkeyAction
{
    QuickInput = 1,
    ShowMainWindow = 2,
    FocusMode = 3,
}

public enum HotkeyState
{
    /// <summary>登録していない（開発用フォルダで TASKDECK_DEV_HOTKEYS が無い・キーの変更中で止めている）。</summary>
    NotRegistered,
    Registered,
    /// <summary>他のアプリが使っていて登録できなかった。</summary>
    InUse,
    /// <summary>設定のキーの文字列を読めなかった。</summary>
    Invalid,
}

/// <summary>Win32 の RegisterHotKey の受け口。本物は <see cref="HwndHotkeyApi"/>、テストでは偽物を渡す。</summary>
public interface IHotkeyApi : IDisposable
{
    /// <summary>登録したキーが押された（引数は id）。UI スレッドで来る。</summary>
    event EventHandler<int>? Pressed;

    /// <summary>登録できたら null、できなければ Win32 のエラーコード。</summary>
    int? Register(int id, uint modifiers, uint virtualKey);

    /// <summary>外せたら true。</summary>
    bool Unregister(int id);
}

/// <summary>
/// グローバルホットキー（F-092・F-093、設計書のリスク表 #3）。settings.Hotkeys の3つを登録し、押されたら Pressed を出す。
/// 登録に失敗したキー（他のアプリが使用中・読めない）は StateOf で分かり、設定の「ショートカット」ページが警告を出す。
/// 設定のキーが変わったら登録し直す。開発用フォルダでは TASKDECK_DEV_HOTKEYS=1 のときだけ登録する（依頼者のキーを奪わない）。
/// 登録と解除は UI スレッドで行う（ホットキーは登録したスレッドの窓に届くため）。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private static readonly HotkeyAction[] Actions = [HotkeyAction.QuickInput, HotkeyAction.ShowMainWindow, HotkeyAction.FocusMode];

    private readonly ISettingsStore _settings;
    private readonly IHotkeyApi _api;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<HotkeyService> _logger;
    private readonly Dictionary<HotkeyAction, HotkeyState> _states = Actions.ToDictionary(a => a, _ => HotkeyState.NotRegistered);
    private readonly HashSet<int> _registered = [];
    private string? _appliedKeys;
    private bool _suspended;
    private bool _started;

    public HotkeyService(ISettingsStore settings, ResidencySwitches switches, IHotkeyApi api, IUiDispatcher dispatcher, ILogger<HotkeyService> logger)
    {
        _settings = settings;
        _api = api;
        _dispatcher = dispatcher;
        _logger = logger;
        IsEnabled = switches.RegisterHotkeys;
        _api.Pressed += OnPressed;
    }

    /// <summary>ホットキーを登録する構成か（開発用フォルダで TASKDECK_DEV_HOTKEYS が無ければ false）。</summary>
    public bool IsEnabled { get; }

    /// <summary>登録したキーが押された（UI スレッド）。</summary>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>
    /// 押されたキーを先に受け取る口（初回起動の2画面目が「押されたか」を見るため。UI スレッド）。
    /// true を返したら Pressed を出さない（クイック入力などを開かない）。使い終わったら null に戻す。
    /// </summary>
    public Func<HotkeyAction, bool>? Intercept { get; set; }

    /// <summary>登録の結果が変わった（UI スレッド）。</summary>
    public event EventHandler? StatesChanged;

    public HotkeyState StateOf(HotkeyAction action) => _states[action];

    public static string SettingOf(HotkeySettings hotkeys, HotkeyAction action) => action switch
    {
        HotkeyAction.QuickInput => hotkeys.QuickInput,
        HotkeyAction.ShowMainWindow => hotkeys.ShowMainWindow,
        _ => hotkeys.FocusMode,
    };

    /// <summary>起動時に1回（UI スレッド）。以後は設定の変更を見て登録し直す。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _settings.Changed += OnSettingsChanged;
        Apply(force: true);
    }

    /// <summary>設定画面でキーを入力している間は外しておく（今のキーを押すと入力の代わりにホットキーが動いてしまうため）。</summary>
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended)
        {
            return;
        }
        _suspended = suspended;
        if (_started)
        {
            Apply(force: true);
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _api.Pressed -= OnPressed;
        UnregisterAll();
        _api.Dispose();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Apply(force: false));

    /// <summary>今の設定で登録し直す。force でなければ、キーの文字列が変わったときだけ。</summary>
    private void Apply(bool force)
    {
        var keys = string.Join('|', Actions.Select(a => SettingOf(_settings.Current.Hotkeys, a)));
        if (!force && keys == _appliedKeys)
        {
            return;
        }
        _appliedKeys = keys;
        UnregisterAll();
        foreach (var action in Actions)
        {
            _states[action] = IsEnabled && !_suspended ? Register(action) : HotkeyState.NotRegistered;
        }
        if (!IsEnabled)
        {
            _logger.LogInformation("開発用フォルダのため、グローバルホットキーを登録しません（TASKDECK_DEV_HOTKEYS=1 で登録）");
        }
        StatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private HotkeyState Register(HotkeyAction action)
    {
        var text = SettingOf(_settings.Current.Hotkeys, action);
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            _logger.LogWarning("ホットキー（{Action}）の設定を読めませんでした: {Keys}", action, text);
            return HotkeyState.Invalid;
        }
        if (_api.Register((int)action, gesture.Modifiers, gesture.VirtualKey) is { } error)
        {
            _logger.LogWarning("ホットキー（{Action}）{Keys} を登録できませんでした（エラー {Error}。他のアプリが使用中の可能性）", action, gesture, error);
            return HotkeyState.InUse;
        }
        _registered.Add((int)action);
        _logger.LogInformation("ホットキー（{Action}）{Keys} を登録しました", action, gesture);
        return HotkeyState.Registered;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered)
        {
            if (!_api.Unregister(id))
            {
                _logger.LogWarning("ホットキー（{Id}）を外せませんでした", id);
            }
        }
        _registered.Clear();
    }

    private void OnPressed(object? sender, int id)
    {
        if (_registered.Contains(id) && Intercept?.Invoke((HotkeyAction)id) != true)
        {
            Pressed?.Invoke(this, (HotkeyAction)id);
        }
    }
}

/// <summary>
/// RegisterHotKey の本物。メッセージ専用の窓（HwndSource）を最初の登録のときに作り、WM_HOTKEY を受ける。
/// 登録しない構成（開発用フォルダ）では窓も作らない。UI スレッドで使う。
/// </summary>
internal sealed class HwndHotkeyApi : IHotkeyApi
{
    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    private HwndSource? _source;

    public event EventHandler<int>? Pressed;

    public int? Register(int id, uint modifiers, uint virtualKey)
    {
        _source ??= CreateSource();
        return NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey)
            ? null
            : Marshal.GetLastPInvokeError();
    }

    public bool Unregister(int id) => _source is not null && NativeMethods.UnregisterHotKey(_source.Handle, id);

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }

    private HwndSource CreateSource()
    {
        var source = new HwndSource(new HwndSourceParameters("TaskDeck.Hotkeys") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        source.AddHook(WndProc);
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            handled = true;
            Pressed?.Invoke(this, wParam.ToInt32());
        }
        return IntPtr.Zero;
    }
}
