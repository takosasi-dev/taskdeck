using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.ViewModels;

/// <summary>
/// 一覧に並ぶ行の親。グループ見出しも「行」として平らに並べる（CollectionView のグループ化は使わない。
/// 仮想化を効かせたまま見出しを混ぜるため）。
/// </summary>
public abstract partial class ListItemViewModel : ObservableObject
{
    /// <summary>選択やキー操作の対象になるか（見出しと入力行は false）。</summary>
    public virtual bool IsSelectable => false;
}

/// <summary>グループ見出し（「期限切れ 2」「今日 5」「今日・9月23日（水）」）。</summary>
public sealed partial class TaskGroupHeaderViewModel(string title, int count, bool isWarning = false) : ListItemViewModel
{
    public string Title { get; } = title;

    public int Count { get; } = count;

    /// <summary>期限切れの見出し（警告アイコン＋赤）。</summary>
    public bool IsWarning { get; } = isWarning;

    public string AutomationName => string.Create(CultureInfo.InvariantCulture, $"{Title} {Count}件");
}

/// <summary>
/// テンプレートから一度に作った一群の見出し（F-15B。同じ TemplateBatchId のタスクが一覧に2件以上あるとき）。
/// 「週次レビュー（9/23 に作成）」。見出しの「まとめて完了」「まとめて削除」は、この見出しの下にある一群の行に効く。
/// </summary>
public sealed partial class TemplateBatchHeaderViewModel(Guid batchId, string name, DateOnly createdOn, int count) : ListItemViewModel
{
    public Guid BatchId { get; } = batchId;

    /// <summary>テンプレートの名前（分からなければ「テンプレート」）。</summary>
    public string Name { get; } = name;

    public string Title { get; } = string.Create(CultureInfo.InvariantCulture, $"{name}（{createdOn.Month}/{createdOn.Day} に作成）");

    public int Count { get; } = count;

    public string AutomationName => string.Create(CultureInfo.InvariantCulture, $"{Title} {Count}件");

    public string CompleteName => $"「{Name}」の一群をまとめて完了";

    public string DeleteName => $"「{Name}」の一群をまとめて削除";
}

/// <summary>
/// 一覧の末尾に置く入力行（Ctrl+N でここにフォーカスが入る）。
/// 普段は「＋ タスクを追加 Ctrl+N」と見えるだけの入力欄で、開く・閉じるの状態は持たない（フォーカスの出入りで消えないように）。
/// </summary>
public sealed partial class AddTaskRowViewModel : ListItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string _text = "";

    public string Hint { get; set; } = "タスクを追加   Ctrl+N";

    public string ExampleHint => "例: 会議資料まとめる 明日 15:00 #仕事 !高";

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public string AutomationName => "タスクを追加";
}

/// <summary>
/// 一覧の行の入れ物。読み直しのたびに中身を差し替える。
/// </summary>
public sealed class RowCollection : ObservableCollection<ListItemViewModel>
{
    /// <summary>これ以下の件数なら1件ずつ差し替える。多いときは Reset 1回にする（1万件ぶんの変更通知の方が重いため）。</summary>
    public const int IncrementalLimit = 1000;

    /// <summary>
    /// 新しい並びにする。少ないときは位置ごとに Replace するので、画面側は見えている行の入れ物を作り直さず
    /// 中身（DataContext）だけを差し替えられる（行のテンプレートを作り直すより軽い）。
    /// </summary>
    public void Update(IReadOnlyList<ListItemViewModel> items)
    {
        if (items.Count > IncrementalLimit || Count > IncrementalLimit)
        {
            ReplaceAll(items);
            return;
        }
        // 同じ物が別の位置へ移るとき（末尾の入力行）は先に取り除き、途中で同じ物が2つ並ばないようにする
        var targets = new Dictionary<ListItemViewModel, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < items.Count; i++)
        {
            targets[items[i]] = i;
        }
        for (var i = Count - 1; i >= 0; i--)
        {
            if (targets.TryGetValue(this[i], out var target) && target != i)
            {
                RemoveAt(i);
            }
        }
        var common = Math.Min(Count, items.Count);
        for (var i = 0; i < common; i++)
        {
            if (!ReferenceEquals(this[i], items[i]))
            {
                this[i] = items[i];
            }
        }
        while (Count > items.Count)
        {
            RemoveAt(Count - 1);
        }
        for (var i = Count; i < items.Count; i++)
        {
            Add(items[i]);
        }
    }

    /// <summary>Reset 1回で丸ごと入れ替える。</summary>
    public void ReplaceAll(IEnumerable<ListItemViewModel> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>プロジェクトの表示用の値。ColorHex はテーマ変換前（ライト）の値。</summary>
public readonly record struct ProjectLook(string Name, string ColorHex);

/// <summary>
/// 行を作るときに要る材料。読み込みのたびに作り直し、作った後は変えない（スレッドプールで行を組み立てるため）。
/// </summary>
public sealed record RowBuildContext(
    IClock Clock,
    IReadOnlyDictionary<Guid, string> TagNames,
    IReadOnlyDictionary<Guid, ProjectLook> Projects,
    bool IsDark,
    bool CompactRows,
    IReadOnlyList<string> SearchTerms)
{
    public bool IsSearching => SearchTerms.Count > 0;

    /// <summary>テンプレートの一群（TemplateBatchId）→ テンプレートの名前。見出しに出す（無ければ「テンプレート」）。</summary>
    public IReadOnlyDictionary<Guid, string> BatchNames { get; init; } = new Dictionary<Guid, string>();
}

/// <summary>一覧の1行（UI 設計書 6.1）。表示に必要な値だけを持ち、View の型（Brush など）は持たない。</summary>
public sealed partial class TaskRowViewModel : ListItemViewModel
{
    /// <summary>1段の字下げ（モックアップのサブタスク行は左端 36px＝親の 8px＋28px）。</summary>
    public const double IndentStep = 28;

    public required TaskItem Task { get; init; }

    public override bool IsSelectable => true;

    public Guid Id => Task.Id;

    /// <summary>表示上の字下げ（0 が親）。</summary>
    public required int Level { get; init; }

    public bool IsSubtask => Level > 0;

    /// <summary>条件には合わないが、合った親の下に添えた行。</summary>
    public required bool IsContext { get; init; }

    /// <summary>行の高さ（親 42／サブタスク 34。詰めて表示は 36）。</summary>
    public required double RowHeight { get; init; }

    public double Indent => Level * IndentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = "";

    /// <summary>行に出す文字（メモだけが一致した検索結果ではメモの抜粋）。</summary>
    public string DisplayTitle => NoteSnippet ?? Title;

    public required bool IsCompleted { get; init; }

    /// <summary>完了か中止（チェックが付いた見た目にする）。</summary>
    public required bool IsClosed { get; init; }

    public required bool IsInTrash { get; init; }

    /// <summary>チェックを操作できるか（ゴミ箱の中は不可）。</summary>
    public bool CanCheck => !IsInTrash;

    public Priority Priority => Task.Priority;

    /// <summary>優先度の記号（なしのときも 22px の欄は空けておく）。</summary>
    public string PrioritySymbol => DisplayText.PrioritySymbol(Task.Priority);

    public required DueDisplay Due { get; init; }

    public string DueText => Due.Text;

    public bool HasDue => Due.HasText;

    public bool IsOverdue => Due.IsOverdue;

    public bool IsDueStrong => Due.IsStrong;

    /// <summary>「#仕事 #買い物」。</summary>
    public required string TagsText { get; init; }

    public bool HasTags => TagsText.Length > 0;

    public required string? ProjectName { get; init; }

    public bool HasProject => !string.IsNullOrEmpty(ProjectName);

    /// <summary>テーマ変換前のプロジェクト色（テーマが変わったら塗り直すため）。</summary>
    public required string? SourceProjectColorHex { get; init; }

    /// <summary>テーマに合わせたプロジェクト色（#RRGGBB）。</summary>
    [ObservableProperty]
    private string? _projectColorHex;

    public bool HasRecurrence => Task.RecurrenceRuleId is not null;

    /// <summary>「1/2」。サブタスクが無ければ null。</summary>
    public required string? SubtaskProgress { get; init; }

    public bool HasSubtasks => SubtaskProgress is not null;

    /// <summary>直下のサブタスクの数（削除済みを除く）と、そのうち閉じた（完了・中止）数。</summary>
    public int SubtaskCount { get; init; }

    public int SubtaskDoneCount { get; init; }

    /// <summary>未完了のサブタスクがある（完了にするとき確認する F-024）。</summary>
    public bool HasOpenSubtasks => SubtaskDoneCount < SubtaskCount;

    /// <summary>進捗バーの割合（0〜1、F-023）。</summary>
    public double SubtaskRatio => SubtaskCount == 0 ? 0 : (double)SubtaskDoneCount / SubtaskCount;

    /// <summary>検索結果のときの出どころ（今日／予定／完了済み…）。</summary>
    public required string? SearchSource { get; init; }

    public bool HasSearchSource => SearchSource is not null;

    /// <summary>タイトルに一致が無く、メモに一致したとき出す本文（「メモ：…」の後ろ）。</summary>
    public required string? NoteSnippet { get; init; }

    public bool HasNoteSnippet => NoteSnippet is not null;

    /// <summary>検索の強調に使う語（空白区切り）。</summary>
    public required string? SearchTerms { get; init; }

    /// <summary>読み上げ（「未完了、優先度高、会議資料まとめる、今日15時」）。</summary>
    public required string AutomationName { get; init; }

    /// <summary>その場で名前を変えている最中。</summary>
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    /// <summary>完了して消えるまでの間（167ms）。</summary>
    [ObservableProperty]
    private bool _isFading;

    /// <summary>一覧で選ばれている（行の見た目の切替用。祖先をたどるバインディングを使わないため）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>ドラッグで運んでいる最中（元の位置に薄く残す UI 設計書 15.5）。</summary>
    [ObservableProperty]
    private bool _isDragging;

    /// <summary>この行の上に落とすと子になる（アクセントの枠で示す）。</summary>
    [ObservableProperty]
    private bool _isDropTarget;

    public void Repaint(bool isDark) =>
        ProjectColorHex = SourceProjectColorHex is { } hex ? ProjectPalette.ForTheme(hex, isDark) : null;

    public static TaskRowViewModel Create(TaskListRow row, int level, RowBuildContext context)
    {
        var task = row.Task;
        var clock = context.Clock;
        var tags = row.TagIds.Count == 0
            ? ""
            : string.Join(" ", row.TagIds.Where(context.TagNames.ContainsKey).Select(id => "#" + context.TagNames[id]));
        var project = task.ProjectId is { } pid && context.Projects.TryGetValue(pid, out var p) ? p : (ProjectLook?)null;

        string? source = null;
        string? snippet = null;
        if (context.IsSearching)
        {
            source = SourceViewName(task, clock);
            var title = TextNormalizer.ForSearch(task.Title);
            if (!context.SearchTerms.Any(t => title.Contains(t, StringComparison.Ordinal)) && task.Notes is { Length: > 0 } notes)
            {
                snippet = Snippet(notes, context.SearchTerms);
            }
        }

        return new TaskRowViewModel
        {
            Task = task,
            Level = level,
            IsContext = row.IsContext,
            RowHeight = level > 0 ? 34 : context.CompactRows ? 36 : 42,
            Title = task.Title,
            IsCompleted = task.Status == TaskItemStatus.Completed,
            IsClosed = !task.IsOpen,
            IsInTrash = task.IsDeleted,
            Due = DisplayText.DueColumn(task, clock),
            TagsText = tags,
            ProjectName = project?.Name,
            SourceProjectColorHex = project?.ColorHex,
            ProjectColorHex = project is { } look ? ProjectPalette.ForTheme(look.ColorHex, context.IsDark) : null,
            SubtaskProgress = row.SubtaskCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{row.SubtaskDoneCount}/{row.SubtaskCount}")
                : null,
            SubtaskCount = row.SubtaskCount,
            SubtaskDoneCount = row.SubtaskDoneCount,
            SearchSource = source,
            NoteSnippet = snippet,
            SearchTerms = context.IsSearching ? string.Join(' ', context.SearchTerms) : null,
            AutomationName = DisplayText.RowAutomationName(task, clock),
        };
    }

    /// <summary>検索結果の「出どころ」（UI 設計書 15.2）。</summary>
    internal static string SourceViewName(TaskItem task, IClock clock)
    {
        if (task.IsDeleted)
        {
            return "ゴミ箱";
        }
        if (!task.IsOpen)
        {
            return "完了済み";
        }
        return TaskRules.Categorize(task, clock) switch
        {
            DueCategory.Overdue or DueCategory.Today => "今日",
            DueCategory.Tomorrow or DueCategory.ThisWeek => "予定",
            _ => "すべて",
        };
    }

    // ponytail: 一致位置は元の文字列に対する単純な探索（NFKC 正規化前の全角違いは抜粋の位置がずれることがある）。
    // 実用上ほとんどの語は当たるので、必要になったら位置対応表を持つ方式に上げる。
    private static string Snippet(string notes, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            var index = notes.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }
            var start = Math.Max(0, index - 12);
            var length = Math.Min(notes.Length - start, 60);
            var text = notes.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ');
            return (start > 0 ? "…" : "") + text + (start + length < notes.Length ? "…" : "");
        }
        // 正規化の違いで元の文字列に見つからないときは先頭を出す
        var head = notes.Replace('\n', ' ').Replace('\r', ' ');
        return head.Length > 60 ? head[..60] + "…" : head;
    }
}
