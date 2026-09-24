using TaskDeck.Core.Entities;

namespace TaskDeck.Core.Abstractions;

/// <summary>
/// 祝日の問い合わせ（同期で即答。中身はキャッシュ）。取得に失敗していれば「祝日なし」として答える。
/// </summary>
public interface IHolidayProvider
{
    bool IsHoliday(DateOnly date);

    /// <summary>祝日名。祝日でなければ null。</summary>
    string? GetName(DateOnly date);
}

/// <summary>
/// 天気の問い合わせ（同期で即答。中身はキャッシュ）。設定が OFF・場所が未設定・まだ取得していない日は null。
/// キャッシュが入れ替わったら DataChangeHub に DataChangeKind.ExternalCache が出る（祝日も同じ）。
/// </summary>
public interface IWeatherProvider
{
    WeatherDay? Get(DateOnly date);
}

/// <summary>
/// 繰り返しの計算（設計書 4.1）。RRULE はローカル時刻で評価し、結果は UTC で返す。
/// 日付のみの期限は結果もローカル 0:00 の UTC。リポジトリのテストで差し替えられるようインタフェースにしてある。
/// </summary>
public interface IRecurrenceEngine
{
    /// <summary>完了時の次回期限（UTC）。BaseKind が期限日基準なら task.DueAt、完了日基準なら completedAtUtc から数える。終了条件に達したら null。</summary>
    DateTime? NextDue(TaskItem task, RecurrenceRule rule, DateTime completedAtUtc);

    /// <summary>スキップ用: 現在の期限の次の発生日時（UTC）。終了条件に達したら null。</summary>
    DateTime? NextDueForSkip(TaskItem task, RecurrenceRule rule);

    /// <summary>設定画面のプレビュー: baseDueUtc の後の発生日時を count 件（「次回」「その次」）。</summary>
    IReadOnlyList<DateTime> Preview(RecurrenceInput rule, DateTime baseDueUtc, bool dueHasTime, int count);

    /// <summary>カレンダーの仮表示: currentDueUtc より後で [fromUtc, toUtc) にある発生日時（UTC）。</summary>
    IReadOnlyList<DateTime> Occurrences(RecurrenceRule rule, DateTime currentDueUtc, bool dueHasTime, DateTime fromUtc, DateTime toUtc);
}
