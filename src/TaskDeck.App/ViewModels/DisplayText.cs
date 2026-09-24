using System.Globalization;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.App.ViewModels;

/// <summary>期限欄の表示（UI 設計書 6.1）。Strong は今日の時刻つきのように濃く出すもの。</summary>
public readonly record struct DueDisplay(string Text, bool IsOverdue, bool IsStrong)
{
    public bool HasText => Text.Length > 0;
}

/// <summary>画面に出す文字列づくり。日付の判定はすべて IClock 経由のローカル日付で行う。</summary>
public static class DisplayText
{
    private static readonly string[] WeekdayNames = ["日", "月", "火", "水", "木", "金", "土"];

    public static string PrioritySymbol(Priority priority) => priority switch
    {
        Priority.Urgent => "!!!!",
        Priority.High => "!!!",
        Priority.Medium => "!!",
        Priority.Low => "!",
        _ => "",
    };

    public static string PriorityName(Priority priority) => priority switch
    {
        Priority.Urgent => "緊急",
        Priority.High => "高",
        Priority.Medium => "中",
        Priority.Low => "低",
        _ => "なし",
    };

    public static string StatusName(TaskItemStatus status) => status switch
    {
        TaskItemStatus.InProgress => "進行中",
        TaskItemStatus.Completed => "完了",
        TaskItemStatus.Cancelled => "中止",
        _ => "未着手",
    };

    public static string Weekday(DateOnly date) => WeekdayNames[(int)date.DayOfWeek];

    /// <summary>トーストなどの文に入れるタイトル（「」付き。長いものは20文字で切り、文の後ろが見切れないようにする）。</summary>
    public static string Quote(string title)
    {
        const int max = 20;
        var text = TextNormalizer.GraphemeCount(title) > max ? TextNormalizer.TruncateGraphemes(title, max) + "…" : title;
        return "「" + text + "」";
    }

    /// <summary>「9月23日（水）」。</summary>
    public static string DateWithWeekday(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Month}月{date.Day}日（{Weekday(date)}）");

    /// <summary>日付ごとのグループ見出し（「今日・9月23日（水）」「明日・…」「昨日・…」、それ以外は日付だけ）。</summary>
    public static string DayHeader(DateOnly day, DateOnly today)
    {
        var date = DateWithWeekday(day);
        return (day.DayNumber - today.DayNumber) switch
        {
            0 => "今日・" + date,
            1 => "明日・" + date,
            -1 => "昨日・" + date,
            _ => date,
        };
    }

    /// <summary>一覧の期限欄（58px）。</summary>
    public static DueDisplay DueColumn(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return new DueDisplay("", false, false);
        }
        var today = clock.LocalToday();
        var day = clock.ToLocalDate(due);
        var local = clock.ToLocal(due);

        if (TaskRules.IsOverdue(task, clock))
        {
            var days = day < today ? today.DayNumber - day.DayNumber : 0;
            var text = days > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{days}日超過")
                : local.ToString("H:mm", CultureInfo.InvariantCulture);
            return new DueDisplay(text, true, true);
        }
        if (day == today)
        {
            return task.DueHasTime
                ? new DueDisplay(local.ToString("H:mm", CultureInfo.InvariantCulture), false, true)
                : new DueDisplay("終日", false, false);
        }
        if (day == today.AddDays(1))
        {
            return new DueDisplay("明日", false, false);
        }
        return new DueDisplay(string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}"), false, false);
    }

    /// <summary>短い期限（「今日」「明日 15:00」「9/25」）。トーストの文に入れる。</summary>
    public static string DueShort(DateTime dueAt, bool hasTime, IClock clock)
    {
        var today = clock.LocalToday();
        var day = clock.ToLocalDate(dueAt);
        var word = (day.DayNumber - today.DayNumber) switch
        {
            0 => "今日",
            1 => "明日",
            _ => string.Create(CultureInfo.InvariantCulture, $"{day.Month}/{day.Day}"),
        };
        return hasTime
            ? word + " " + clock.ToLocal(dueAt).ToString("H:mm", CultureInfo.InvariantCulture)
            : word;
    }

    /// <summary>詳細ペインの期限（「9月22日（火） 15:00」）。期限なしは「なし」。</summary>
    public static string DueLong(DateTime? dueAt, bool hasTime, IClock clock)
    {
        if (dueAt is not { } due)
        {
            return "なし";
        }
        var day = clock.ToLocalDate(due);
        var text = DateWithWeekday(day);
        return hasTime
            ? text + " " + clock.ToLocal(due).ToString("H:mm", CultureInfo.InvariantCulture)
            : text;
    }

    /// <summary>読み上げ用の期限（「今日15時」「3日超過」）。</summary>
    public static string? DueForSpeech(TaskItem task, IClock clock)
    {
        if (task.DueAt is not { } due)
        {
            return null;
        }
        var today = clock.LocalToday();
        var day = clock.ToLocalDate(due);
        var local = clock.ToLocal(due);
        var time = task.DueHasTime ? string.Create(CultureInfo.InvariantCulture, $"{local.Hour}時{(local.Minute > 0 ? $"{local.Minute}分" : "")}") : "";
        if (TaskRules.IsOverdue(task, clock))
        {
            var days = today.DayNumber - day.DayNumber;
            return days > 0 ? string.Create(CultureInfo.InvariantCulture, $"{days}日超過") : "期限切れ " + time;
        }
        var dayWord = day == today ? "今日"
            : day == today.AddDays(1) ? "明日"
            : string.Create(CultureInfo.InvariantCulture, $"{day.Month}月{day.Day}日");
        return dayWord + time;
    }

    /// <summary>行の読み上げ（「未完了、優先度高、会議資料まとめる、今日15時」）。</summary>
    public static string RowAutomationName(TaskItem task, IClock clock)
    {
        var parts = new List<string>(4) { task.IsOpen ? "未完了" : StatusName(task.Status) };
        if (task.Priority != Priority.None)
        {
            parts.Add("優先度" + PriorityName(task.Priority));
        }
        parts.Add(task.Title);
        if (DueForSpeech(task, clock) is { } due)
        {
            parts.Add(due);
        }
        return string.Join("、", parts);
    }

    /// <summary>通知の相対指定（分）の文言。</summary>
    public static string ReminderName(int? offsetMinutes) => offsetMinutes switch
    {
        null => "なし",
        0 => "期限の時刻",
        5 => "5分前",
        15 => "15分前",
        30 => "30分前",
        60 => "1時間前",
        1440 => "1日前",
        { } m => string.Create(CultureInfo.InvariantCulture, $"{m}分前"),
    };

    /// <summary>
    /// 所要時間の文言（「45分」「1時間」「1時間15分」）。詳細ペインの選択肢も同じ形にする。
    /// カレンダーで端をドラッグして入る 75分・105分 のような、選択肢に無い値も同じ形で出す。
    /// </summary>
    public static string DurationName(int? minutes)
    {
        if (minutes is not { } total)
        {
            return "なし";
        }
        var (hours, rest) = Math.DivRem(total, 60);
        return (hours, rest) switch
        {
            (0, _) => string.Create(CultureInfo.InvariantCulture, $"{rest}分"),
            (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{hours}時間"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{hours}時間{rest}分"),
        };
    }

    /// <summary>「作成 9/20 10:12 ・ 更新 9/22 09:40」。</summary>
    public static string CreatedUpdated(DateTime createdUtc, DateTime updatedUtc, IClock clock)
    {
        var created = clock.ToLocal(createdUtc);
        var updated = clock.ToLocal(updatedUtc);
        return string.Create(CultureInfo.InvariantCulture,
            $"作成 {created.Month}/{created.Day} {created:HH:mm} ・ 更新 {updated.Month}/{updated.Day} {updated:HH:mm}");
    }
}
