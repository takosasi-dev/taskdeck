using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Results;

namespace TaskDeck.Core.Abstractions;

/// <summary>
/// タスクの読み書き。書き込みはメソッド1回＝1トランザクションで、成功したら DataChangeHub に通知する。
/// 書き込み系は変更前のスナップショット（ChangeSet）を返すので、呼び出し側は UndoService に渡すだけで取り消せる。
///
/// 書き込み時にリポジトリが必ず守る不変条件:
/// - タイトルは前後空白と改行を除き、500文字（書記素）まで。空になる変更は無視して元の値を残す
/// - 状態を完了／中止にしたら CompletedAt=現在、未完了に戻したら CompletedAt=null
/// - 完了にしたタスクに繰り返しがあれば IRecurrenceEngine で次回を1件作る（同じ系列に未完了があれば作らない）。
///   ルールの CompletedCount を1増やす。子タスクの構成も未完了で複製する
/// - RemindOffsetMinutes があれば RemindAt を期限から再計算（日付のみの期限は既定リマインド時刻が基準）。
///   RemindAt が変わったら NotifiedAt=null
/// - Depth は 0〜2。自分の子孫を親にできない
/// - 親をソフトデリートしたら、削除しない子は親なし（ParentTaskId=null）に昇格し Depth を詰める
/// </summary>
public interface ITaskRepository
{
    // ---- 読み取り ----

    /// <summary>条件に合うタスク。並びは query.SortKey 順。IncludeSubtasks なら合ったタスクの子孫も IsContext=true で含める。</summary>
    Task<IReadOnlyList<TaskListRow>> QueryAsync(TaskQuery query, CancellationToken ct = default);

    Task<TaskItem?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TaskItem>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    Task<TaskDetail?> GetDetailAsync(Guid id, CancellationToken ct = default);

    Task<ViewCounts> GetViewCountsAsync(CancellationToken ct = default);

    /// <summary>振り返り用。fromUtc 以降に完了したもの（null なら全期間）。</summary>
    Task<IReadOnlyList<CompletedTaskFact>> GetCompletedFactsAsync(DateTime? fromUtc, CancellationToken ct = default);

    /// <summary>通知すべきもの: 未完了・削除済みでない・RemindAt ≤ nowUtc・NotifiedAt が null。RemindAt 順。</summary>
    Task<IReadOnlyList<TaskItem>> GetDueRemindersAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>カレンダー用: DueAt が [fromUtc, toUtc) のもの（削除済みを除く）。includeClosed なら完了・中止も。</summary>
    Task<IReadOnlyList<TaskItem>> GetInDueRangeAsync(DateTime fromUtc, DateTime toUtc, bool includeClosed, CancellationToken ct = default);

    /// <summary>カレンダーの仮表示用: 未完了で繰り返しのある実体タスクとルール。</summary>
    Task<IReadOnlyList<RecurringTaskInfo>> GetOpenRecurringAsync(CancellationToken ct = default);

    // ---- 書き込み ----

    Task<TaskMutationResult> AddAsync(NewTaskRequest request, CancellationToken ct = default);

    /// <summary>まとめて追加（1トランザクション）。要素の Id と ParentTaskId で親子を指定できる（親を先に並べる）。</summary>
    Task<TaskMutationResult> AddManyAsync(IReadOnlyList<NewTaskRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// 1件の値を書き換える。mutate で変えてよいのは Title, Notes, Status, Priority, DueAt, DueHasTime,
    /// RemindAt, RemindOffsetMinutes, ProjectId, DurationMinutes だけ（他を変えると InvalidOperationException）。
    /// </summary>
    Task<TaskMutationResult> UpdateAsync(Guid id, Action<TaskItem> mutate, CancellationToken ct = default);

    /// <summary>複数件に同じ変更をかける（一括操作）。変えてよい列は UpdateAsync と同じ。</summary>
    Task<TaskMutationResult> UpdateManyAsync(IReadOnlyCollection<Guid> ids, Action<TaskItem> mutate, CancellationToken ct = default);

    /// <summary>完了（true）／未着手に戻す（false）。完了時の繰り返しの次回は Created に入る。</summary>
    Task<TaskMutationResult> SetCompletedAsync(IReadOnlyCollection<Guid> ids, bool completed, CancellationToken ct = default);

    /// <summary>繰り返しの今回分を完了にせず、期限だけ次回へ送る（F-037）。</summary>
    Task<TaskMutationResult> SkipOccurrenceAsync(Guid id, CancellationToken ct = default);

    /// <summary>タグを置き換える（外したものは TaskTag をソフトデリート、付け直しは復活）。</summary>
    Task<TaskMutationResult> SetTagsAsync(Guid id, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    /// <summary>一括でタグを足す（既に付いているものはそのまま）。</summary>
    Task<TaskMutationResult> AddTagsAsync(IReadOnlyCollection<Guid> ids, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    /// <summary>繰り返しを設定／変更（null で解除。解除しても生成済みのタスクは残る F-036）。</summary>
    Task<TaskMutationResult> SetRecurrenceAsync(Guid id, RecurrenceInput? recurrence, CancellationToken ct = default);

    /// <summary>親を変える（null で親なし）。3階層を超える・循環する場合は Fail。sortOrder が null なら新しい兄弟の末尾。</summary>
    Task<OperationResult<TaskMutationResult>> SetParentAsync(Guid id, Guid? newParentId, double? sortOrder, CancellationToken ct = default);

    /// <summary>手動の並び順を変える。値は SortOrderMath.Between で作る。隙間が詰まったら兄弟を振り直す。</summary>
    Task<TaskMutationResult> ReorderAsync(Guid id, double sortOrder, CancellationToken ct = default);

    /// <summary>複製（F-016）。元のすぐ後ろに置き、状態は未着手に戻す。</summary>
    Task<TaskMutationResult> DuplicateAsync(Guid id, bool includeSubtasks, CancellationToken ct = default);

    Task<TaskMutationResult> SoftDeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    Task<TaskMutationResult> RestoreAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>ゴミ箱から物理削除（取り消せない。呼ぶ前に確認ダイアログを出す）。消した件数。</summary>
    Task<int> PurgeAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>削除日時が deletedBeforeUtc より前のゴミ箱を物理削除（null ならゴミ箱を空にする）。消した件数。</summary>
    Task<int> PurgeDeletedAsync(DateTime? deletedBeforeUtc, CancellationToken ct = default);

    /// <summary>期限切れの未完了をすべて「今日（日付のみ）」へ移す（F-137）。</summary>
    Task<TaskMutationResult> CarryOverOverdueAsync(CancellationToken ct = default);

    /// <summary>
    /// 通知を出したと記録する（F-086）。まだ出していない（NotifiedAt が null）で RemindAt が notifiedAtUtc 以前のものだけ書く
    /// （読んでから書くまでに「30分後」などでリマインドが先へ動いたタスクを、出した扱いにしない）。
    /// </summary>
    Task<TaskMutationResult> MarkNotifiedAsync(IReadOnlyCollection<Guid> ids, DateTime notifiedAtUtc, CancellationToken ct = default);

    /// <summary>再通知（スヌーズ）: RemindAt を指定時刻に、NotifiedAt=null。相対指定は外す。</summary>
    Task<TaskMutationResult> SnoozeAsync(Guid id, DateTime remindAtUtc, CancellationToken ct = default);

    /// <summary>ChangeSet の変更前へ戻す（1トランザクション）。</summary>
    Task ApplyUndoAsync(ChangeSet changes, CancellationToken ct = default);
}
