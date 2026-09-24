using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Residency;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Startup;

public enum FirstRunStep
{
    Welcome = 1,
    Hotkey = 2,
    Parse = 3,
}

/// <summary>
/// 初回起動の3画面（S-15、F-181〜186、UI 設計書 24、モックアップ Onboarding.dc.html）の中身。読ませずに手を動かさせる。
/// ① ようこそ →「はじめる」
/// ② クイック入力のホットキーを実際に押させる。押されるまで「次へ」は無効、押されたら緑のチェックにして少し後に自動で③へ。
///    押されたかは HotkeyService.Intercept で受ける（②の間だけ持ち、QuickInput なら true を返してクイック入力を開かせない。
///    他のキーは横取りしない）。②を離れるとき・窓を閉じるとき（<see cref="Detach"/>）に必ず外す。
///    キーが登録できていなければ理由を出す（StatesChanged で出し直す。設定でキーを変えたら新しいキーで待つ）
/// ③ 1行を QuickInputParser で読み、読み取った日付の語と日付を見せる。Enter で登録して終わり（窓を閉じる）。
/// どの画面からもスキップできる（窓を閉じるだけ。終えた記録は窓が書く）。
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly HotkeyService _hotkeys;
    private readonly ISettingsStore _settings;
    private readonly QuickInputParser _parser;
    private readonly ITaskRepository _tasks;
    private readonly UndoService _undo;
    private readonly IClock _clock;
    private readonly ILogger<FirstRunViewModel> _logger;
    private readonly Func<HotkeyAction, bool> _intercept;
    private bool _submitting;
    private bool _detached;

    public FirstRunViewModel(
        HotkeyService hotkeys,
        ISettingsStore settings,
        QuickInputParser parser,
        ITaskRepository tasks,
        UndoService undo,
        IClock clock,
        ILogger<FirstRunViewModel> logger)
    {
        _hotkeys = hotkeys;
        _settings = settings;
        _parser = parser;
        _tasks = tasks;
        _undo = undo;
        _clock = clock;
        _logger = logger;
        _intercept = OnHotkey;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsHotkey), nameof(IsParse), nameof(Dots), nameof(StepName))]
    private FirstRunStep _step = FirstRunStep.Welcome;

    public bool IsWelcome => Step == FirstRunStep.Welcome;

    public bool IsHotkey => Step == FirstRunStep.Hotkey;

    public bool IsParse => Step == FirstRunStep.Parse;

    /// <summary>下のステップ表示（3点。現在地だけ true で、幅18の棒になる）。</summary>
    public IReadOnlyList<bool> Dots => [IsWelcome, IsHotkey, IsParse];

    /// <summary>ステップ表示の読み上げ名（「手順 2 / 3」）。</summary>
    public string StepName => string.Create(CultureInfo.InvariantCulture, $"手順 {(int)Step} / 3");

    // ---- ② ホットキー ----

    /// <summary>キーの表示（「Ctrl」「+」「Shift」「+」「Space」。「+」はキーの枠を付けない）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _keyCaps = [];

    /// <summary>キーの数（「この 3 キーでタスクを足せます」）。</summary>
    [ObservableProperty]
    private int _keyCount;

    /// <summary>キーが押された（緑のチェックにして、少し後に③へ）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaiting), nameof(ShowHotkeyNote))]
    private bool _keyPressed;

    /// <summary>キーが登録できていないときの説明（登録できていれば空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWaiting), nameof(ShowHotkeyNote))]
    private string _hotkeyNote = "";

    /// <summary>説明が「このままでは押しても届かない」問題か（赤で出す）。</summary>
    [ObservableProperty]
    private bool _hotkeyNoteIsProblem;

    /// <summary>「いま押してみてください」を出す（押される前で、登録できている）。</summary>
    public bool ShowWaiting => !KeyPressed && HotkeyNote.Length == 0;

    /// <summary>登録できていない説明を出す（押される前）。</summary>
    public bool ShowHotkeyNote => !KeyPressed && HotkeyNote.Length > 0;

    /// <summary>押されてから③へ進むまでの間（緑のチェックを見せる時間。テストでは 0 にする）。</summary>
    public TimeSpan AdvanceDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    // ---- ③ 日付の読み取り ----

    /// <summary>入力欄の文字（変換中の文字を含む。読み取りは画面が Refresh を呼んだときだけ）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private string _text = "";

    /// <summary>読み取った期限（「9/23（水）」「9/23（水） 15:00」）。読み取れなければ null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDue))]
    private string? _dueLabel;

    public bool HasDue => DueLabel is not null;

    /// <summary>入力のうち、日付として読んだ語（アクセント色にする。Start と Length は入力の添字）。</summary>
    [ObservableProperty]
    private IReadOnlyList<ParsedToken> _dateTokens = [];

    public bool CanSubmit => !string.IsNullOrWhiteSpace(Text);

    /// <summary>①「はじめる」。</summary>
    public void Begin() => GoTo(FirstRunStep.Hotkey);

    /// <summary>②「次へ」（キーが押された後だけ）。</summary>
    public void Next()
    {
        if (IsHotkey && KeyPressed)
        {
            GoTo(FirstRunStep.Parse);
        }
    }

    /// <summary>画面を切り替える。②に入るときにキーの横取りを始め、②を出るときにやめる。</summary>
    public void GoTo(FirstRunStep step)
    {
        if (_detached || step == Step)
        {
            return;
        }
        if (IsHotkey)
        {
            LeaveHotkey();
        }
        Step = step;
        if (IsHotkey)
        {
            EnterHotkey();
        }
    }

    /// <summary>窓を閉じるとき（閉じ方によらず必ず呼ぶ）。キーの横取りをやめ、以後は何もしない。</summary>
    public void Detach()
    {
        if (IsHotkey)
        {
            LeaveHotkey();
        }
        _detached = true;
    }

    /// <summary>今の入力を読み直す（IME の変換中は呼ばない）。</summary>
    public void Refresh()
    {
        var parsed = _parser.Parse(Text);
        DateTokens = [.. parsed.Tokens.Where(t => t.Kind is ParsedTokenKind.Date or ParsedTokenKind.Time or ParsedTokenKind.Recurrence)];
        DueLabel = parsed.DueAt is { } due ? DueText(due, parsed.DueHasTime) : null;
    }

    /// <summary>
    /// Enter: 1件登録する。登録したら true（窓を閉じる）。空・IME の変換中（composing）・登録の途中は何もしない。
    /// 読み取れない部分はタイトルに残る（パーサは入力を捨てない）。登録に失敗したら例外のまま上げ、入力は残す。
    /// </summary>
    public async Task<bool> SubmitAsync(bool composing = false)
    {
        if (composing || _submitting || !CanSubmit)
        {
            return false;
        }
        _submitting = true;
        try
        {
            var request = _parser.Parse(Text).ToRequest();
            var result = await _tasks.AddAsync(request);
            var created = result.Created.FirstOrDefault();
            _undo.Record(result, DisplayText.Quote(created?.Title ?? request.Title) + "を追加しました", UndoKind.Other, showToast: false);
            _logger.LogInformation("初回起動の3画面目でタスクを追加しました {TaskId}", created?.Id);
            return true;
        }
        finally
        {
            _submitting = false;
        }
    }

    private void EnterHotkey()
    {
        KeyPressed = false;
        _hotkeys.Intercept = _intercept;
        _hotkeys.StatesChanged += OnStatesChanged;
        RefreshHotkey();
    }

    private void LeaveHotkey()
    {
        if (ReferenceEquals(_hotkeys.Intercept, _intercept))
        {
            _hotkeys.Intercept = null;
        }
        _hotkeys.StatesChanged -= OnStatesChanged;
    }

    /// <summary>押されたホットキーを先に受ける（UI スレッド）。QuickInput だけ受け取り、他はそのまま通す。</summary>
    private bool OnHotkey(HotkeyAction action)
    {
        if (action != HotkeyAction.QuickInput)
        {
            return false;
        }
        if (!KeyPressed)
        {
            KeyPressed = true;
            _logger.LogInformation("初回起動: クイック入力のキーが押されました");
            _ = AdvanceAfterDelayAsync();
        }
        return true;
    }

    // 待つのは画面の都合なので Task.Delay（IClock ではない）。待つ間に閉じたり、「次へ」で先に進んでいたら何もしない
    private async Task AdvanceAfterDelayAsync()
    {
        await Task.Delay(AdvanceDelay);
        Next();
    }

    private void OnStatesChanged(object? sender, EventArgs e) => RefreshHotkey();

    private void RefreshHotkey()
    {
        var keys = HotkeyService.SettingOf(_settings.Current.Hotkeys, HotkeyAction.QuickInput);
        var names = HotkeyGesture.Display(keys).Split(" + ", StringSplitOptions.RemoveEmptyEntries);
        KeyCaps = [.. names.SelectMany((name, i) => i == 0 ? new[] { name } : ["+", name])];
        KeyCount = names.Length;
        (HotkeyNote, HotkeyNoteIsProblem) = _hotkeys.StateOf(HotkeyAction.QuickInput) switch
        {
            HotkeyState.InUse => ("このキーは他のアプリが使っているため、登録できませんでした。下から別のキーを選べます。", true),
            HotkeyState.Invalid => ("設定のキーを読み取れませんでした。下から選び直せます。", true),
            HotkeyState.NotRegistered when !_hotkeys.IsEnabled => ("開発用フォルダで動かしているため、キーを登録していません（TASKDECK_DEV_HOTKEYS=1 で登録）。", false),
            _ => ("", false),
        };
    }

    /// <summary>「9/23（水）」「9/23（水） 15:00」（クイック入力のチップと同じ形）。</summary>
    private string DueText(DateTime due, bool hasTime)
    {
        var day = _clock.ToLocalDate(due);
        var text = string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}（{DisplayText.Weekday(day)}）");
        return hasTime ? text + " " + _clock.ToLocal(due).ToString("H:mm", CultureInfo.InvariantCulture) : text;
    }
}
