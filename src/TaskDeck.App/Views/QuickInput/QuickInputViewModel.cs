using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TaskDeck.App.Services;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.QuickInput;

public enum QuickInputChipKind
{
    Due,
    Tag,
    Project,
    Priority,
    Recurrence,
}

/// <summary>
/// 解釈のプレビューのチップ1つ（F-073）。Symbol は前に付ける印（タグの「#」・優先度の「!!!」）。
/// 期限・優先度・プロジェクトはクリックで直せる（F-074。既存のピッカーを使う）。IsCorrected は手で直した値。
/// </summary>
public sealed record QuickInputChip(QuickInputChipKind Kind, string Text, string? Symbol = null, Priority Priority = Priority.None, bool IsCorrected = false)
{
    public bool CanEdit => Kind is QuickInputChipKind.Due or QuickInputChipKind.Priority or QuickInputChipKind.Project;

    /// <summary>チップの頭のアイコン（Icons.xaml のキー。タグと優先度は印の文字で出すので無し）。</summary>
    public string? IconKey => Kind switch
    {
        QuickInputChipKind.Due => "IconCalendar",
        QuickInputChipKind.Project => "IconBriefcase",
        QuickInputChipKind.Recurrence => "IconRepeat",
        _ => null,
    };

    /// <summary>読み上げ・UI Automation 用の名前（「期限 9/23（水） 15:00」）。</summary>
    public string AccessibleName => Kind switch
    {
        QuickInputChipKind.Due => "期限 " + Text,
        QuickInputChipKind.Tag => "タグ " + Text,
        QuickInputChipKind.Project => "プロジェクト " + Text,
        QuickInputChipKind.Priority => "優先度 " + Text,
        _ => "繰り返し " + Text,
    };

    /// <summary>UI Automation の項目名（ItemsControl の項目は ToString が名前になる）。</summary>
    public override string ToString() => AccessibleName;
}

/// <summary>
/// クイック入力（S-02、F-071〜075、UI 設計書 5.2）の中身。1行を QuickInputParser で解釈してチップで見せ、Enter で登録する。
/// - <b>パースに失敗しても必ず登録する</b>（パーサは例外を投げず、読めない部分はタイトルに残る）。登録に失敗したら入力は消さない
/// - チップの手動補正は、文字列を書き換えずに「補正」として持ち、登録のときに上書きする（入力を消すか履歴を呼ぶと外れる）
/// - ↑ / ↓ で直近の入力 10 件（AppStateKeys.QuickInputHistory、新しい順の JSON）
/// - 登録の直後（空の入力で）Ctrl+Z を押すと、その登録を取り消す（その後に別の操作をしていれば取り消さない）
/// 窓は使い回すので、開くたびに OpenAsync で入力を空にする。
/// </summary>
public sealed partial class QuickInputViewModel(
    QuickInputParser parser,
    ITaskRepository tasks,
    IProjectRepository projects,
    IAppStateRepository state,
    UndoService undo,
    IClock clock,
    ILogger<QuickInputViewModel> logger) : ObservableObject
{
    public const int HistoryCapacity = 10;

    private List<string> _history = [];
    private Task? _historyLoading;
    private int _historyIndex = -1;
    private string _draft = "";
    private DueCorrection? _due;
    private Priority? _priority;
    private ProjectCorrection? _project;
    private ChangeSet? _lastAdded;
    private string _lastAddedTitle = "";

    /// <summary>入力欄の文字（変換中の文字を含む。解釈は画面が Refresh を呼んだときだけ）。</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>プレビューの行に出す短い知らせ（「取り消しました」など）。無ければ空。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExamples))]
    private string _notice = "";

    public ObservableCollection<QuickInputChip> Chips { get; } = [];

    /// <summary>チップが1つも無いときは、プレビューの行に書き方の例を出す。</summary>
    public bool ShowExamples => Chips.Count == 0 && Notice.Length == 0;

    /// <summary>直せるチップがあれば、右端に「クリックで修正」を出す。</summary>
    public bool ShowFixHint => Chips.Any(c => c.CanEdit);

    public IReadOnlyList<string> History => _history;

    /// <summary>窓を出すたびに呼ぶ。入力と補正を空にする（履歴は初回だけ読む）。</summary>
    public async Task OpenAsync()
    {
        Text = "";
        Notice = "";
        _historyIndex = -1;
        _draft = "";
        ClearCorrections();
        Refresh();
        try
        {
            await EnsureHistoryAsync();
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException || ex.InnerException is DbException)
        {
            // 履歴が読めなくても入力はできる（↑ が効かないだけ。次に開いたときに読み直す）
            logger.LogWarning(ex, "クイック入力の履歴を読めませんでした");
        }
    }

    /// <summary>今の入力を解釈してチップを作り直す（IME の変換中は呼ばない）。</summary>
    public void Refresh()
    {
        if (Text.Length == 0)
        {
            ClearCorrections();
        }
        else
        {
            Notice = "";
        }
        var request = BuildRequest();
        Chips.Clear();
        if (request is not null)
        {
            foreach (var chip in ChipsFor(request))
            {
                Chips.Add(chip);
            }
        }
        OnPropertyChanged(nameof(ShowExamples));
        OnPropertyChanged(nameof(ShowFixHint));
    }

    /// <summary>
    /// 登録する。登録したら true（画面は閉じる）。空なら何もしない。
    /// 登録に失敗したら例外のまま上げ、入力は消さない。登録は履歴に左右されない（履歴の読み書きに失敗しても、
    /// 登録は済ませて警告だけ残す。登録の後で例外を上げると、入力が残って二重に登録されかねないため）。
    /// </summary>
    public async Task<bool> SubmitAsync()
    {
        var text = Text;
        if (BuildRequest() is not { } request)
        {
            return false;
        }
        var result = await tasks.AddAsync(request);
        var created = result.Created.FirstOrDefault();
        var title = created?.Title ?? request.Title;
        undo.Record(result, DisplayText.Quote(title) + "を追加しました", UndoKind.Other, showToast: false);
        _lastAdded = result.Changes;
        _lastAddedTitle = title;
        logger.LogInformation("クイック入力でタスクを追加しました {TaskId}", created?.Id);
        try
        {
            await SaveHistoryAsync(text);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException || ex.InnerException is DbException)
        {
            logger.LogWarning(ex, "クイック入力の履歴を保存できませんでした（タスクは追加済み）");
        }
        return true;
    }

    /// <summary>
    /// 空の入力で Ctrl+Z: 直前にここで登録したものを取り消す。取り消したら true（入力があるときや、
    /// その後に別の操作がされたときは false。入力欄の文字の取り消しに任せる）。
    /// </summary>
    public async Task<bool> UndoLastAsync()
    {
        if (Text.Length > 0 || _lastAdded is null || !ReferenceEquals(undo.Stack.Peek()?.Changes, _lastAdded))
        {
            return false;
        }
        _lastAdded = null;
        if (!await undo.UndoLatestAsync())
        {
            return false;
        }
        Notice = DisplayText.Quote(_lastAddedTitle) + "の追加を取り消しました";
        return true;
    }

    /// <summary>↑: ひとつ古い入力を出す。出したら true。</summary>
    public bool RecallOlder()
    {
        if (_historyIndex + 1 >= _history.Count)
        {
            return false;
        }
        if (_historyIndex < 0)
        {
            _draft = Text;
        }
        _historyIndex++;
        ShowRecalled(_history[_historyIndex]);
        return true;
    }

    /// <summary>↓: ひとつ新しい入力へ（最新の先は打ちかけの入力に戻る）。出したら true。</summary>
    public bool RecallNewer()
    {
        if (_historyIndex < 0)
        {
            return false;
        }
        _historyIndex--;
        ShowRecalled(_historyIndex < 0 ? _draft : _history[_historyIndex]);
        return true;
    }

    // ---- チップからの手動補正（F-074） ----

    /// <summary>日付ピッカーに入れる今の期限（ローカル）。</summary>
    public (DateOnly? Date, TimeOnly? Time) CurrentDue() =>
        BuildRequest() is { DueAt: { } due } request
            ? (clock.ToLocalDate(due), request.DueHasTime ? TimeOnly.FromDateTime(clock.ToLocal(due)) : null)
            : (null, null);

    public Priority CurrentPriority() => BuildRequest()?.Priority ?? Priority.None;

    /// <summary>プロジェクトピッカーに入れる今のプロジェクト（名前で書いたものは Id が分からないので null）。</summary>
    public Guid? CurrentProjectId() => _project?.Id;

    /// <summary>date=null は期限を消す、time=null は終日。</summary>
    public void CorrectDue(DateOnly? date, TimeOnly? time)
    {
        _due = date is not { } day
            ? new DueCorrection(null, false)
            : time is { } at ? new DueCorrection(clock.LocalToUtc(day, at), true) : new DueCorrection(TaskRules.DateOnlyDue(day, clock), false);
        Refresh();
    }

    public void CorrectPriority(Priority priority)
    {
        _priority = priority;
        Refresh();
    }

    /// <summary>projectId=null は「なし」。</summary>
    public async Task CorrectProjectAsync(Guid? projectId)
    {
        var name = projectId is { } id ? (await projects.GetAsync(id))?.Name : null;
        _project = new ProjectCorrection(projectId, name);
        Refresh();
    }

    /// <summary>登録する内容（解釈＋手動補正）。空なら null。</summary>
    internal NewTaskRequest? BuildRequest()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return null;
        }
        var request = parser.Parse(Text).ToRequest();
        if (_due is { } due)
        {
            request = request with { DueAt = due.DueAt, DueHasTime = due.DueAt is not null && due.HasTime };
        }
        if (_priority is { } priority)
        {
            request = request with { Priority = priority };
        }
        if (_project is { } project)
        {
            request = request with { ProjectId = project.Id, ProjectName = null };
        }
        return request;
    }

    private IEnumerable<QuickInputChip> ChipsFor(NewTaskRequest request)
    {
        if (request.DueAt is { } due)
        {
            yield return new QuickInputChip(QuickInputChipKind.Due, DueLabel(due, request.DueHasTime), IsCorrected: _due is not null);
        }
        foreach (var tag in request.TagNames)
        {
            yield return new QuickInputChip(QuickInputChipKind.Tag, tag, "#");
        }
        var projectName = _project is { } project ? project.Name : request.ProjectName;
        if (projectName is not null)
        {
            yield return new QuickInputChip(QuickInputChipKind.Project, projectName, IsCorrected: _project is not null);
        }
        if (request.Priority != Priority.None)
        {
            yield return new QuickInputChip(
                QuickInputChipKind.Priority,
                DisplayText.PriorityName(request.Priority),
                DisplayText.PrioritySymbol(request.Priority),
                request.Priority,
                _priority is not null);
        }
        if (request.Recurrence is { } recurrence)
        {
            yield return new QuickInputChip(QuickInputChipKind.Recurrence, RecurrenceText.Describe(recurrence.RRule));
        }
    }

    /// <summary>「9/23（水） 15:00」（モックアップの形）。</summary>
    private string DueLabel(DateTime due, bool hasTime)
    {
        var day = clock.ToLocalDate(due);
        var text = string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}（{DisplayText.Weekday(day)}）");
        return hasTime ? text + " " + clock.ToLocal(due).ToString("H:mm", CultureInfo.InvariantCulture) : text;
    }

    private void ShowRecalled(string text)
    {
        ClearCorrections();
        Text = text;
        Refresh();
    }

    private void ClearCorrections()
    {
        _due = null;
        _priority = null;
        _project = null;
    }

    /// <summary>
    /// 履歴は最初の1回だけ読む（書くのはここだけなので、以後はメモリの方が正しい。読み込みの途中で登録されても消えない）。
    /// 読めなかった（DB の失敗）ときは次にまた読み直す。
    /// </summary>
    private Task EnsureHistoryAsync()
    {
        if (_historyLoading is { IsFaulted: true } or { IsCanceled: true })
        {
            _historyLoading = null;
        }
        return _historyLoading ??= LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var json = await state.GetAsync(AppStateKeys.QuickInputHistory);
        if (string.IsNullOrEmpty(json))
        {
            return;
        }
        try
        {
            _history = JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "クイック入力の履歴を読めませんでした。空にして続けます");
        }
    }

    private async Task SaveHistoryAsync(string text)
    {
        await EnsureHistoryAsync();
        var entry = text.Trim();
        _history.RemoveAll(h => string.Equals(h, entry, StringComparison.Ordinal));
        _history.Insert(0, entry);
        if (_history.Count > HistoryCapacity)
        {
            _history.RemoveRange(HistoryCapacity, _history.Count - HistoryCapacity);
        }
        _historyIndex = -1;
        await state.SetAsync(AppStateKeys.QuickInputHistory, JsonSerializer.Serialize(_history));
    }

    private sealed record DueCorrection(DateTime? DueAt, bool HasTime);

    private sealed record ProjectCorrection(Guid? Id, string? Name);
}
