using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskDeck.App.ViewModels;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.Views.Focus;

/// <summary>フォーカスモードに出すサブタスク1件（この画面では主な操作。チェックは 17px）。</summary>
public sealed partial class FocusSubtask(Guid id, string title, bool isDone) : ObservableObject
{
    public Guid Id { get; } = id;

    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckName))]
    private bool _isDone = isDone;

    public string CheckName => Title + (IsDone ? " を未完了に戻す" : " を完了にする");

    /// <summary>取りやめたとき、チェックの見た目を状態に合わせ直す。</summary>
    public void RefreshCheck() => OnPropertyChanged(nameof(IsDone));

    public override string ToString() => CheckName;
}

/// <summary>
/// いま出している1件（UI 設計書 25.2 の順: プロジェクト・優先度・タグ → タイトル → 期限と残り時間 → メモの頭 → サブタスク）。
/// 面はテーマに関わらず暗いので、プロジェクト色はダークの対応色にしておく。
/// </summary>
public sealed partial class FocusCard : ObservableObject
{
    public required TaskItem Task { get; init; }

    public Guid TaskId => Task.Id;

    public string Title => Task.Title;

    public string? ProjectName { get; init; }

    /// <summary>ダークの面に合わせたプロジェクト色（#RRGGBB）。</summary>
    public string? ProjectColorHex { get; init; }

    public bool HasProject => ProjectName is not null;

    public Priority Priority => Task.Priority;

    public string PrioritySymbol => DisplayText.PrioritySymbol(Task.Priority);

    public string TagsText { get; init; } = "";

    public bool HasMeta => HasProject || Priority != Priority.None || TagsText.Length > 0;

    public string? NotesHead { get; init; }

    public bool HasNotes => !string.IsNullOrEmpty(NotesHead);

    public IReadOnlyList<FocusSubtask> Subtasks { get; init; } = [];

    public bool HasSubtasks => Subtasks.Count > 0;

    public bool HasOpenSubtasks => Subtasks.Any(s => !s.IsDone);

    public int SubtaskDone => Subtasks.Count(s => s.IsDone);

    public string SubtaskProgress => string.Create(CultureInfo.InvariantCulture, $"{SubtaskDone} / {Subtasks.Count}");

    public double SubtaskRatio => Subtasks.Count == 0 ? 0 : (double)SubtaskDone / Subtasks.Count;

    public bool HasDue => Task.DueAt is not null;

    /// <summary>「15:00 まで」「今日まで」「9/20 まで」。</summary>
    [ObservableProperty]
    private string _dueLabel = "";

    /// <summary>「あと 1 時間 12 分」「今日中」「2 日超過」。</summary>
    [ObservableProperty]
    private string _remainingLabel = "";

    [ObservableProperty]
    private bool _isOverdue;

    /// <summary>期限と残り時間を今の時刻で作り直す（画面が30秒ごとに呼ぶ）。</summary>
    public void UpdateTimes(IClock clock)
    {
        DueLabel = FocusText.DueLabel(Task, clock);
        RemainingLabel = FocusText.Remaining(Task, clock);
        IsOverdue = TaskRules.IsOverdue(Task, clock);
    }

    /// <summary>サブタスクのチェックが変わった後に、数と進捗を出し直す。</summary>
    public void RecountSubtasks()
    {
        OnPropertyChanged(nameof(SubtaskDone));
        OnPropertyChanged(nameof(SubtaskProgress));
        OnPropertyChanged(nameof(SubtaskRatio));
        OnPropertyChanged(nameof(HasOpenSubtasks));
    }
}

/// <summary>フォーカスモードの文言（期限・残り時間・メモの頭）と並び順（F-176）。</summary>
public static class FocusText
{
    public const int NotesHeadLength = 200;

    /// <summary>
    /// 並び順（F-176）: 期限の近い順 → 優先度の高い順（手動の並び順は見ない）。同じなら作った順。
    /// 日付だけの期限は「その日の終わり」を締め切りとみなす（今日 11:00 のタスクを、今日中のタスクより先に出す）。
    /// </summary>
    public static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, TaskItem> task, IClock clock) =>
        items
            .OrderBy(x => Deadline(task(x), clock))
            .ThenByDescending(x => task(x).Priority)
            .ThenBy(x => task(x).CreatedAt)
            .ThenBy(x => task(x).Id);

    public static DateTime Deadline(TaskItem task, IClock clock) => task.DueAt switch
    {
        null => DateTime.MaxValue,
        { } due when task.DueHasTime => due,
        { } due => clock.LocalDayStartUtc(clock.ToLocalDate(due).AddDays(1)),
    };

    /// <summary>「15:00 まで」（今日の時刻）・「9/20 15:00 まで」・「今日まで」・「9/20 まで」。期限なしは空。</summary>
    public static string DueLabel(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return "";
        }
        var day = clock.ToLocalDate(due);
        var date = day == clock.LocalToday() ? "" : string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}");
        if (task.DueHasTime)
        {
            var time = clock.ToLocal(due).ToString("H:mm", CultureInfo.InvariantCulture);
            return (date.Length > 0 ? date + " " : "") + time + " まで";
        }
        return date.Length > 0 ? date + " まで" : "今日まで";
    }

    /// <summary>「あと 1 時間 12 分」・「30 分超過」・「今日中」・「2 日超過」。期限なしは空。</summary>
    public static string Remaining(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return "";
        }
        if (task.DueHasTime)
        {
            var left = due - clock.UtcNow;
            return left > TimeSpan.Zero ? "あと " + Span(left) : Span(-left) + "超過";
        }
        var days = clock.LocalToday().DayNumber - clock.ToLocalDate(due).DayNumber;
        return days switch
        {
            > 0 => string.Create(CultureInfo.InvariantCulture, $"{days} 日超過"),
            0 => "今日中",
            _ => string.Create(CultureInfo.InvariantCulture, $"あと {-days} 日"),
        };
    }

    /// <summary>メモの頭（前後の空白を除いて 200 文字まで）。</summary>
    public static string? NotesHead(string? notes)
    {
        var text = notes?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return text.Length <= NotesHeadLength ? text : text[..NotesHeadLength] + "…";
    }

    /// <summary>「45 分」「1 時間」「1 時間 12 分」「2 日」（分は切り捨て、1分未満は 1 分）。</summary>
    private static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)span.TotalMinutes);
        if (minutes >= 24 * 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes / (24 * 60)} 日");
        }
        var (hours, rest) = Math.DivRem(minutes, 60);
        return (hours, rest) switch
        {
            (0, _) => string.Create(CultureInfo.InvariantCulture, $"{rest} 分"),
            (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{hours} 時間"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{hours} 時間 {rest} 分"),
        };
    }
}
