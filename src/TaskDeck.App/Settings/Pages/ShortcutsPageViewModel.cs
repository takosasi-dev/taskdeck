using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.Residency;
using TaskDeck.Core.Settings;

namespace TaskDeck.App.Settings.Pages;

/// <summary>グローバルホットキー1つぶんの行（設定カード1枚）。</summary>
public sealed partial class HotkeyRow(HotkeyAction action, string title, string description, string iconKey) : ObservableObject
{
    public HotkeyAction Action => action;

    public string Title => title;

    public string Description => description;

    public string IconKey => iconKey;

    /// <summary>「変更」ボタンの読み上げ名（「クイック入力を開く を変更」）。</summary>
    public string ChangeLabel => title + " を変更";

    /// <summary>キーの表示（「Ctrl + Shift + Space」。入力中は案内）。</summary>
    [ObservableProperty]
    private string _keys = "";

    /// <summary>カードの下に出す一言（登録できなかった・読めない・開発用フォルダ・入力の誤り）。無ければ空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    /// <summary>Status が警告（赤で出す）か。</summary>
    [ObservableProperty]
    private bool _hasProblem;

    [ObservableProperty]
    private bool _isRecording;

    public bool HasStatus => Status.Length > 0;
}

/// <summary>
/// ショートカット（F-123、UI 設計書 17章）。グローバルホットキーの3つを見せて変えられるようにする（担当: 波3-H。波1-D は表示だけ）。
/// 変え方: 「変更」を押す → 使いたい組み合わせを押す（Esc でやめる）。Ctrl・Alt・Win のどれかを含むこと、3つで重ならないことを確かめてから保存する。
/// 入力の間はホットキーを外しておく（今のキーを押すと入力の代わりにホットキーが動いてしまうため）。
/// 登録できなかったキー（他のアプリが使用中）は HotkeyService の状態を見て警告を出す（設計書のリスク表 #3「設定画面へ誘導する」）。
/// </summary>
public sealed partial class ShortcutsPageViewModel : ObservableObject
{
    /// <summary>
    /// Windows の操作に使う組み合わせ（登録できてしまうものもあり、取ると全部のアプリで効かなくなる）。
    /// 入力中に窓を閉じようと Alt+F4 を押したときなどに、そのまま保存しないため。
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alt+F4", "Alt+Tab", "Alt+Shift+Tab", "Ctrl+Alt+Tab", "Ctrl+Alt+Delete",
    };

    private readonly ISettingsStore _settings;
    private readonly HotkeyService? _hotkeys;
    private bool _attached;

    public ShortcutsPageViewModel(ISettingsStore settings, HotkeyService? hotkeys = null)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        Rows =
        [
            new HotkeyRow(HotkeyAction.QuickInput, "クイック入力を開く", "どのアプリからでも、1行でタスクを足す窓を出します", "IconAdd"),
            new HotkeyRow(HotkeyAction.ShowMainWindow, "TaskDeck を前に出す", "トレイに引っ込んでいるウィンドウを呼び戻します", "IconMonitor"),
            new HotkeyRow(HotkeyAction.FocusMode, "フォーカスモードを開く", "今やる1件だけを大きく出します", "IconFocus"),
        ];
        Refresh();
    }

    public IReadOnlyList<HotkeyRow> Rows { get; }

    public string QuickInput => HotkeyGesture.Display(_settings.Current.Hotkeys.QuickInput);

    public string ShowMainWindow => HotkeyGesture.Display(_settings.Current.Hotkeys.ShowMainWindow);

    public string FocusMode => HotkeyGesture.Display(_settings.Current.Hotkeys.FocusMode);

    /// <summary>キーを入力している行（無ければ null）。</summary>
    [ObservableProperty]
    private HotkeyRow? _recording;

    /// <summary>ページが出たとき（登録の結果が変わったら表示を直す）。</summary>
    public void Attach()
    {
        if (_attached || _hotkeys is null)
        {
            return;
        }
        _attached = true;
        _hotkeys.StatesChanged += OnStatesChanged;
        Refresh();
    }

    /// <summary>ページが消えたとき（入力の途中ならやめてホットキーを戻す）。</summary>
    public void Detach()
    {
        CancelRecording();
        if (_attached && _hotkeys is not null)
        {
            _hotkeys.StatesChanged -= OnStatesChanged;
        }
        _attached = false;
    }

    public void StartRecording(HotkeyRow row)
    {
        CancelRecording();
        Recording = row;
        row.IsRecording = true;
        _hotkeys?.SetSuspended(true);
        Refresh();
    }

    public void CancelRecording()
    {
        if (Recording is null)
        {
            return;
        }
        Recording.IsRecording = false;
        Recording = null;
        _hotkeys?.SetSuspended(false);
        Refresh();
    }

    /// <summary>
    /// 入力中に押された組み合わせ（"Ctrl+Alt+K"。使えないキーなら null）。条件に合えば保存して入力を終える（true）。
    /// 合わなければ理由を出して入力を続ける（false）。
    /// </summary>
    public bool Assign(string? keys)
    {
        if (Recording is not { } row)
        {
            return false;
        }
        if (keys is null || !HotkeyGesture.TryParse(keys, out var gesture))
        {
            return Reject(row, "このキーは使えません。英字・数字・F1〜F24・Space などと組み合わせてください");
        }
        if (!gesture.HasCommandModifier)
        {
            return Reject(row, "Ctrl・Alt・Win のどれかと組み合わせてください（Shift だけでは文字の入力とぶつかります）");
        }
        if (Reserved.Contains(gesture.ToString()))
        {
            return Reject(row, "Windows が使う組み合わせなので選べません");
        }
        if (Rows.FirstOrDefault(r => r != row && Same(HotkeyService.SettingOf(_settings.Current.Hotkeys, r.Action), gesture)) is { } other)
        {
            return Reject(row, $"「{other.Title}」と同じキーです");
        }
        var text = gesture.ToString();
        _settings.Update(s =>
        {
            switch (row.Action)
            {
                case HotkeyAction.QuickInput:
                    s.Hotkeys.QuickInput = text;
                    break;
                case HotkeyAction.ShowMainWindow:
                    s.Hotkeys.ShowMainWindow = text;
                    break;
                default:
                    s.Hotkeys.FocusMode = text;
                    break;
            }
        });
        CancelRecording();
        OnPropertyChanged(nameof(QuickInput));
        OnPropertyChanged(nameof(ShowMainWindow));
        OnPropertyChanged(nameof(FocusMode));
        return true;
    }

    private static bool Same(string text, HotkeyGesture gesture) => HotkeyGesture.TryParse(text, out var other) && other == gesture;

    private static bool Reject(HotkeyRow row, string reason)
    {
        row.Status = reason;
        row.HasProblem = true;
        return false;
    }

    private void OnStatesChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        foreach (var row in Rows)
        {
            if (row.IsRecording)
            {
                row.Keys = "キーを押してください";
                row.Status = "使いたい組み合わせを押します（Esc でやめる）";
                row.HasProblem = false;
                continue;
            }
            row.Keys = HotkeyGesture.Display(HotkeyService.SettingOf(_settings.Current.Hotkeys, row.Action));
            (row.Status, row.HasProblem) = StatusOf(row.Action);
        }
    }

    private (string Text, bool IsProblem) StatusOf(HotkeyAction action)
    {
        if (_hotkeys is null || Recording is not null)
        {
            return ("", false);
        }
        return _hotkeys.StateOf(action) switch
        {
            HotkeyState.InUse => ("他のアプリがこのキーを使っているため、登録できませんでした。「変更」で別のキーにしてください", true),
            HotkeyState.Invalid => ("設定のキーを読めませんでした。「変更」で選び直してください", true),
            HotkeyState.NotRegistered when !_hotkeys.IsEnabled => ("開発用フォルダで動かしているため登録していません（TASKDECK_DEV_HOTKEYS=1 で登録）", false),
            _ => ("", false),
        };
    }
}
