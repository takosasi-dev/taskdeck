using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Results;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.ImportExport;

/// <summary>読み込み方（F-105）。</summary>
public enum ImportMode
{
    /// <summary>今のデータの後ろに足す。Id はすべて振り直し、同じ名前のタグ・プロジェクトは今あるものに寄せる。</summary>
    Append,

    /// <summary>今のデータを消して、ファイルの中身に入れ替える（Id もそのまま）。書き込む前にバックアップを1世代とる。</summary>
    Replace,
}

/// <summary>読み込んで検査を通したファイル（まだ DB には書いていない）。件数は置換の確認ダイアログに出す。</summary>
public sealed class ImportPackage
{
    internal ImportPackage(ExportDocument document, IReadOnlyDictionary<Guid, int> taskDepths, IReadOnlyDictionary<Guid, int> itemDepths)
    {
        Document = document;
        TaskDepths = taskDepths;
        ItemDepths = itemDepths;
    }

    /// <summary>書き出した日時（UTC）。</summary>
    public DateTime ExportedAt => Document.ExportedAt;

    /// <summary>タスクの件数（ゴミ箱を含む）。</summary>
    public int TaskCount => Document.Tasks.Count;

    public int ProjectCount => Document.Projects.Count(p => p.DeletedAt is null);

    public int TagCount => Document.Tags.Count(t => t.DeletedAt is null);

    public int TemplateCount => Document.Templates.Count(t => t.DeletedAt is null);

    internal ExportDocument Document { get; }

    internal IReadOnlyDictionary<Guid, int> TaskDepths { get; }

    internal IReadOnlyDictionary<Guid, int> ItemDepths { get; }
}

/// <summary>
/// 読み込みの結果。件数は新しく入れた行（タスクはゴミ箱を含む。プロジェクト・タグ・テンプレートは削除済みの名残を数えない）。
/// Merged* は同じ名前の既存に寄せた数（追加のときだけ）。Skipped は入れなかったものの説明（画面にそのまま出せる）。
/// BackupPath は置換の前にとったバックアップ（置換で、DB ファイルがあったときだけ）。
/// </summary>
public sealed record ImportSummary(
    ImportMode Mode,
    int TaskCount,
    int ProjectCount,
    int TagCount,
    int TemplateCount,
    int MergedProjectCount,
    int MergedTagCount,
    IReadOnlyList<string> Skipped,
    string? BackupPath);

/// <summary>
/// JSON インポート（F-105）。2段階で使う:
/// 1. <see cref="ReadAsync"/> でファイルを読み、全体を検査する（DB には触らない）。問題があれば、どの種類の何件目かを返す
/// 2. <see cref="ImportAsync"/> で書き込む。1トランザクションなので、途中で失敗・取り消しても何も書かれない
/// 置換の確認ダイアログ（F-144）は呼び出し側（画面）が 1 と 2 の間に出す。
/// 書き込みは AuditInterceptor を通すので、UpdatedAt・SyncState（Pending）・検索キーは読み込んだ時点の値になる。
/// どちらも中身はスレッドプールで走らせる（UI スレッドからそのまま await してよい）。
/// </summary>
public sealed partial class JsonImporter(
    IDbContextFactory<TaskDeckDbContext> factory,
    IClock clock,
    DataChangeHub hub,
    ILogger<JsonImporter> logger,
    Func<string?> backupBeforeReplace)
{
    public const long DefaultMaxFileBytes = 100L * 1024 * 1024;
    public const int DefaultMaxTasks = 100_000;

    private const string NotExportFile = "TaskDeck で書き出したファイルではありません。";

    /// <summary>ファイルの大きさの上限（NFR 6.5: 100MB）。</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>タスクの件数の上限（NFR 6.5: 10万件）。</summary>
    public int MaxTasks { get; init; } = DefaultMaxTasks;

    /// <summary>1回の SaveChanges で書く行数。この単位で進み具合を知らせ、取り消しを確かめる。</summary>
    internal int ChunkSize { get; init; } = 2000;

    [GeneratedRegex(@"^\$\.(?<list>\w+)\[(?<index>\d+)\](?:\.(?<field>\w+))?")]
    private static partial Regex JsonPathPattern();

    /// <summary>
    /// ファイルを読んで検査する（DB には触らない）。失敗の Error はそのまま画面に出せる。
    /// 大きさの上限は長さの分かるストリーム（FileStream・MemoryStream）で確かめる。
    /// </summary>
    public Task<OperationResult<ImportPackage>> ReadAsync(Stream input, CancellationToken ct = default) =>
        Task.Run(() => ReadCoreAsync(input, ct), ct);

    /// <summary>
    /// 検査を通したファイルを書き込む。置換では、書き込む前にバックアップを1世代とる。
    /// 取り消し（ct）は OperationCanceledException で返し、そのときも何も書かれていない。
    /// </summary>
    public Task<OperationResult<ImportSummary>> ImportAsync(
        ImportPackage package,
        ImportMode mode,
        IProgress<DataTransferProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => ImportCoreAsync(package, mode, progress, ct), ct);

    private async Task<OperationResult<ImportPackage>> ReadCoreAsync(Stream input, CancellationToken ct)
    {
        if (input.CanSeek && input.Length > MaxFileBytes)
        {
            return OperationResult<ImportPackage>.Fail(
                $"ファイルが大きすぎます（{Megabytes(input.Length)} MB）。読み込めるのは {Megabytes(MaxFileBytes)} MB までです。");
        }

        ExportDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<ExportDocument>(input, ExportJson.Options, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "読み込むファイルを JSON として読めませんでした");
            return OperationResult<ImportPackage>.Fail(DescribeJsonError(ex));
        }
        if (document is null)
        {
            return OperationResult<ImportPackage>.Fail(NotExportFile);
        }
        // 配列が null（"tasks": null）でも空として扱う
        document.Projects ??= [];
        document.Tags ??= [];
        document.RecurrenceRules ??= [];
        document.Tasks ??= [];
        document.TaskTags ??= [];
        document.Templates ??= [];
        document.TemplateItems ??= [];

        var taskDepths = new Dictionary<Guid, int>();
        var itemDepths = new Dictionary<Guid, int>();
        if (ImportValidator.Validate(document, MaxTasks, taskDepths, itemDepths) is { } error)
        {
            logger.LogWarning("読み込むファイルの検査で止めました: {Error}", error);
            return OperationResult<ImportPackage>.Fail(error);
        }
        return OperationResult<ImportPackage>.Success(new ImportPackage(document, taskDepths, itemDepths));
    }

    private async Task<OperationResult<ImportSummary>> ImportCoreAsync(
        ImportPackage package,
        ImportMode mode,
        IProgress<DataTransferProgress>? progress,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string? backupPath = null;
        if (mode == ImportMode.Replace)
        {
            progress?.Report(new DataTransferProgress("置き換える前のバックアップをとっています"));
            try
            {
                backupPath = backupBeforeReplace();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                // 戻せる状態を作れないなら、消して入れ替えることはしない
                logger.LogError(ex, "置換の前のバックアップに失敗したため、置換をやめました");
                return OperationResult<ImportSummary>.Fail("置き換える前のバックアップをとれなかったので、置き換えをやめました。データは変わっていません。");
            }
        }
        ct.ThrowIfCancellationRequested();

        Rows rows;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (mode == ImportMode.Replace)
            {
                progress?.Report(new DataTransferProgress("今のデータを消しています"));
                await DeleteAllAsync(db, ct);
            }
            rows = await BuildRowsAsync(db, package, mode, ct);
            await WriteAsync(db, rows, progress, ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            return WriteFailed(ex, mode);
        }
        catch (SqliteException ex)
        {
            return WriteFailed(ex, mode);
        }

        Publish();
        var summary = new ImportSummary(
            mode,
            rows.Tasks.Count,
            rows.Projects.Count(p => p.DeletedAt is null),
            rows.Tags.Count(t => t.DeletedAt is null),
            rows.Templates.Count(t => t.DeletedAt is null),
            rows.MergedProjects,
            rows.MergedTags,
            rows.Skipped,
            backupPath);
        logger.LogInformation(
            "JSON を読み込みました（{Mode}）: タスク {Tasks} 件・プロジェクト {Projects} 件（既存に寄せた {MergedProjects} 件）・タグ {Tags} 件（既存に寄せた {MergedTags} 件）・テンプレート {Templates} 件・飛ばしたもの {Skipped} 種類（{Elapsed} ms）",
            ModeName(mode),
            summary.TaskCount,
            summary.ProjectCount,
            summary.MergedProjectCount,
            summary.TagCount,
            summary.MergedTagCount,
            summary.TemplateCount,
            summary.Skipped.Count,
            watch.ElapsedMilliseconds);
        return OperationResult<ImportSummary>.Success(summary);
    }

    /// <summary>置換: 子の表から順に全部消す。キャッシュ・端末の状態（AppState）・同期のメタ情報は残す。</summary>
    private static async Task DeleteAllAsync(TaskDeckDbContext db, CancellationToken ct)
    {
        await db.TaskTags.ExecuteDeleteAsync(ct);
        await db.Tasks.ExecuteDeleteAsync(ct);
        await db.RecurrenceRules.ExecuteDeleteAsync(ct);
        await db.TemplateItems.ExecuteDeleteAsync(ct);
        await db.Templates.ExecuteDeleteAsync(ct);
        await db.Tags.ExecuteDeleteAsync(ct);
        await db.Projects.ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// ファイルの行を書き込む形にする。置換は Id をそのまま、追加は Id を振り直して参照（親子・プロジェクト・タグ・繰り返し・
    /// 系列・テンプレートの一群・テンプレートの項目）を付け替え、同じ名前のプロジェクト・タグは今あるものに寄せ、並びは今ある末尾の後ろに置く。
    /// </summary>
    private async Task<Rows> BuildRowsAsync(TaskDeckDbContext db, ImportPackage package, ImportMode mode, CancellationToken ct)
    {
        var doc = package.Document;
        var append = mode == ImportMode.Append;
        var rows = new Rows();

        var projectsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var tagsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        double rootEnd = 0, projectEnd = 0, templateEnd = 0;
        if (append)
        {
            // 同じ名前が複数あれば、アーカイブしていない・並びが前のものに寄せる（TaskRepository の名前の解決と同じ）
            var projects = await db.Projects.AsNoTracking()
                .Where(p => p.DeletedAt == null)
                .OrderBy(p => p.IsArchived).ThenBy(p => p.SortOrder)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct);
            foreach (var p in projects)
            {
                projectsByName.TryAdd(ImportValidator.NameOf(p.Name), p.Id);
            }
            var tags = await db.Tags.AsNoTracking().Where(t => t.DeletedAt == null).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
            foreach (var t in tags)
            {
                tagsByName.TryAdd(ImportValidator.TagNameOf(t.Name), t.Id);
            }
            // 並びの末尾は MaxSortOrder と同じく削除済みも含めて数える（プロジェクトだけは ProjectRepository に合わせて生きているもの）
            rootEnd = await db.Tasks.Where(t => t.ParentTaskId == null).MaxAsync(t => (double?)t.SortOrder, ct) ?? 0;
            projectEnd = await db.Projects.Where(p => p.DeletedAt == null).MaxAsync(p => (double?)p.SortOrder, ct) ?? 0;
            templateEnd = await db.Templates.MaxAsync(t => (double?)t.SortOrder, ct) ?? 0;
        }

        // ---- プロジェクト・タグ（追加では、生きているものは同じ名前の既存に寄せる） ----
        var projectIds = new Dictionary<Guid, Guid>();
        foreach (var r in doc.Projects)
        {
            var name = ImportValidator.NameOf(r.Name);
            if (append && r.DeletedAt is null && projectsByName.TryGetValue(name, out var existingId))
            {
                projectIds[r.Id] = existingId;
                rows.MergedProjects++;
                continue;
            }
            var project = new Project
            {
                Id = NewId(r.Id),
                Name = name,
                IconKey = ImportValidator.Trimmed(r.IconKey),
                SortOrder = r.SortOrder,
                IsArchived = r.IsArchived,
                CreatedAt = r.CreatedAt,
                DeletedAt = r.DeletedAt,
            };
            project.ColorHex = ImportValidator.Trimmed(r.ColorHex) ?? project.ColorHex;
            projectIds[r.Id] = project.Id;
            rows.Projects.Add(project);
        }

        var tagIds = new Dictionary<Guid, Guid>();
        foreach (var r in doc.Tags)
        {
            var name = ImportValidator.TagNameOf(r.Name);
            if (append && r.DeletedAt is null && tagsByName.TryGetValue(name, out var existingId))
            {
                tagIds[r.Id] = existingId;
                rows.MergedTags++;
                continue;
            }
            var tag = new Tag { Id = NewId(r.Id), Name = name, CreatedAt = r.CreatedAt, DeletedAt = r.DeletedAt };
            tag.ColorHex = ImportValidator.Trimmed(r.ColorHex) ?? tag.ColorHex;
            tagIds[r.Id] = tag.Id;
            rows.Tags.Add(tag);
        }

        // ---- 繰り返し ----
        var ruleIds = new Dictionary<Guid, Guid>();
        foreach (var r in doc.RecurrenceRules)
        {
            var rule = new RecurrenceRule
            {
                Id = NewId(r.Id),
                RRule = r.RRule!.Trim(),
                AnchorAt = r.AnchorAt!.Value,
                EndKind = r.EndKind,
                EndDate = r.EndDate,
                MaxOccurrences = r.MaxOccurrences,
                CompletedCount = r.CompletedCount,
                BaseKind = r.BaseKind,
                CreatedAt = r.CreatedAt,
                DeletedAt = r.DeletedAt,
            };
            ruleIds[r.Id] = rule.Id;
            rows.Rules.Add(rule);
        }

        // ---- タスク（親から順に。系列とテンプレートの一群も振り直して、読み込んだ分が既存の系列と混ざらないようにする） ----
        var taskIds = doc.Tasks.ToDictionary(t => t.Id, t => NewId(t.Id));
        var seriesIds = new Dictionary<Guid, Guid>();
        var batchIds = new Dictionary<Guid, Guid>();
        foreach (var r in doc.Tasks.OrderBy(t => package.TaskDepths[t.Id]))
        {
            var (dueAt, dueHasTime) = DueOf(r);
            rows.Tasks.Add(new TaskItem
            {
                Id = taskIds[r.Id],
                Title = ImportValidator.TitleOf(r.Title),
                Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
                Status = r.Status,
                Priority = r.Priority,
                DueAt = dueAt,
                DueHasTime = dueHasTime,
                RemindAt = r.RemindAt,
                RemindOffsetMinutes = r.RemindOffsetMinutes,
                NotifiedAt = r.NotifiedAt,
                CompletedAt = r.CompletedAt,
                ParentTaskId = r.ParentTaskId is { } parentId ? taskIds[parentId] : null,
                Depth = package.TaskDepths[r.Id],
                ProjectId = r.ProjectId is { } projectId ? projectIds[projectId] : null,
                RecurrenceRuleId = r.RecurrenceRuleId is { } ruleId ? ruleIds[ruleId] : null,
                RecurrenceSeriesId = r.RecurrenceSeriesId is { } seriesId ? SeriesOf(seriesId) : null,
                DurationMinutes = r.DurationMinutes,
                TemplateBatchId = r.TemplateBatchId is { } batchId ? Fresh(batchIds, batchId) : null,
                SortOrder = r.SortOrder,
                CreatedAt = r.CreatedAt,
                DeletedAt = r.DeletedAt,
            });
        }

        var links = new HashSet<(Guid TaskId, Guid TagId)>();
        var duplicateLinks = 0;
        foreach (var r in doc.TaskTags)
        {
            var key = (taskIds[r.TaskId], tagIds[r.TagId]);
            if (!links.Add(key))
            {
                duplicateLinks++;
                continue;
            }
            rows.TaskTags.Add(new TaskTag { TaskId = key.Item1, TagId = key.Item2, CreatedAt = r.CreatedAt, DeletedAt = r.DeletedAt });
        }
        if (duplicateLinks > 0)
        {
            rows.Skipped.Add(Text($"重なっていたタスクとタグの対応 {duplicateLinks:N0} 件"));
        }

        // ---- テンプレートと項目（タグは Id の一覧で持つので付け替える。ファイルに無いタグは外す） ----
        var droppedTagRefs = 0;
        var templateIds = new Dictionary<Guid, Guid>();
        foreach (var r in doc.Templates)
        {
            var template = new TaskTemplate
            {
                Id = NewId(r.Id),
                Name = ImportValidator.NameOf(r.Name),
                Description = ImportValidator.Trimmed(r.Description),
                IconKey = ImportValidator.Trimmed(r.IconKey),
                ColorHex = ImportValidator.Trimmed(r.ColorHex),
                AnchorLabel = ImportValidator.Trimmed(r.AnchorLabel),
                DefaultProjectId = r.DefaultProjectId is { } projectId ? projectIds[projectId] : null,
                DefaultTagIds = MapTags(r.DefaultTagIds),
                UseCount = r.UseCount,
                LastUsedAt = r.LastUsedAt,
                SortOrder = r.SortOrder,
                CreatedAt = r.CreatedAt,
                DeletedAt = r.DeletedAt,
            };
            templateIds[r.Id] = template.Id;
            rows.Templates.Add(template);
        }

        var itemIds = doc.TemplateItems.ToDictionary(i => i.Id, i => NewId(i.Id));
        foreach (var r in doc.TemplateItems.OrderBy(i => package.ItemDepths[i.Id]))
        {
            rows.Items.Add(new TaskTemplateItem
            {
                Id = itemIds[r.Id],
                TemplateId = templateIds[r.TemplateId],
                Title = ImportValidator.TitleOf(r.Title),
                Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
                Priority = r.Priority,
                DueOffsetDays = r.DueOffsetDays,
                DueTime = ImportValidator.TryParseTime(r.DueTime, out var dueTime) ? dueTime : null,
                RemindOffsetMinutes = r.RemindOffsetMinutes,
                DurationMinutes = r.DurationMinutes,
                ParentItemId = r.ParentItemId is { } parentId ? itemIds[parentId] : null,
                Depth = package.ItemDepths[r.Id],
                TagIds = MapTags(r.TagIds),
                SortOrder = r.SortOrder,
                CreatedAt = r.CreatedAt,
                DeletedAt = r.DeletedAt,
            });
        }
        if (droppedTagRefs > 0)
        {
            rows.Skipped.Add(Text($"テンプレートが指していた、ファイルに無いタグ {droppedTagRefs:N0} 件"));
        }

        if (append)
        {
            PlaceAfter(rows.Projects, projectEnd, p => p.SortOrder, (p, v) => p.SortOrder = v);
            PlaceAfter(rows.Tasks.Where(t => t.ParentTaskId is null), rootEnd, t => t.SortOrder, (t, v) => t.SortOrder = v);
            PlaceAfter(rows.Templates, templateEnd, t => t.SortOrder, (t, v) => t.SortOrder = v);
        }
        return rows;

        Guid NewId(Guid old) => append ? Guid.CreateVersion7() : old;

        // 系列の Id はふつう最初のタスクの Id なので、そのタスクがファイルにあれば同じ付け替え先にそろえる
        Guid SeriesOf(Guid old) => taskIds.TryGetValue(old, out var mapped) ? mapped : Fresh(seriesIds, old);

        // 追加では、同じ古い Id には毎回同じ新しい Id を返す（置換ではそのまま）
        Guid Fresh(Dictionary<Guid, Guid> memo, Guid old)
        {
            if (!append)
            {
                return old;
            }
            if (!memo.TryGetValue(old, out var id))
            {
                id = Guid.CreateVersion7();
                memo[old] = id;
            }
            return id;
        }

        List<Guid> MapTags(List<Guid>? old)
        {
            var mapped = new List<Guid>();
            foreach (var id in old ?? [])
            {
                if (tagIds.TryGetValue(id, out var newId))
                {
                    mapped.Add(newId);
                }
                else
                {
                    droppedTagRefs++;
                }
            }
            return mapped;
        }
    }

    /// <summary>
    /// 期限。時刻ありは UTC のまま。日付のみは dueDate（書き出した PC のローカル日付）を、この PC の 0:00 にする
    /// （同じタイムゾーンなら元の値と同じ。ちがうタイムゾーンでも日付がずれない）。
    /// </summary>
    private (DateTime? DueAt, bool DueHasTime) DueOf(TaskRecord r)
    {
        if (r.DueHasTime && r.DueAt is { } at)
        {
            return (at, true);
        }
        if (r.DueDate is { } date)
        {
            return (clock.LocalDayStartUtc(date), false);
        }
        return r.DueAt is { } dateOnly ? (clock.LocalDayStartUtc(clock.ToLocalDate(dateOnly)), false) : (null, false);
    }

    /// <summary>親の表から順に、ChunkSize 行ずつ書く（1万行を1回の SaveChanges に載せない。取り消しもこの単位で効く）。</summary>
    private async Task WriteAsync(TaskDeckDbContext db, Rows rows, IProgress<DataTransferProgress>? progress, CancellationToken ct)
    {
        var total = rows.Total;
        var done = 0;
        progress?.Report(new DataTransferProgress("書き込んでいます", 0, total));
        foreach (var chunk in rows.InInsertOrder().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            db.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            done += chunk.Length;
            progress?.Report(new DataTransferProgress("書き込んでいます", done, total));
        }
    }

    /// <summary>追加するものを、今ある並びの末尾から順に置く（ファイルの中での順は保つ）。</summary>
    private static void PlaceAfter<T>(IEnumerable<T> items, double end, Func<T, double> get, Action<T, double> set)
    {
        var i = 0;
        foreach (var item in items.OrderBy(get).ToList())
        {
            set(item, end + (++i * SortOrderMath.Step));
        }
    }

    /// <summary>コミット後の通知。購読側の例外で読み込みを失敗扱いにしない（書き込みは済んでいる）。</summary>
    private void Publish()
    {
        try
        {
            hub.Publish(DataChangeKind.Tasks | DataChangeKind.Projects | DataChangeKind.Tags | DataChangeKind.Templates);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "変更通知の購読側で例外が起きました");
        }
    }

    private static string ModeName(ImportMode mode) => mode == ImportMode.Replace ? "置換" : "追加";

    private OperationResult<ImportSummary> WriteFailed(Exception ex, ImportMode mode)
    {
        logger.LogError(ex, "JSON の書き込みに失敗したため、変更を戻しました（{Mode}）", ModeName(mode));
        return OperationResult<ImportSummary>.Fail("書き込みに失敗しました。データは読み込む前のままです（詳しくはログにあります）。");
    }

    /// <summary>JSON の読み取りの失敗を、どの種類の何件目か・何行目かの文にする。</summary>
    internal static string DescribeJsonError(JsonException ex)
    {
        var where = new List<string>();
        if (ex.Path is { } path
            && JsonPathPattern().Match(path) is { Success: true } match
            && ImportValidator.KindOf(match.Groups["list"].Value) is { } kind
            && int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var field = match.Groups["field"];
            where.Add(field.Success ? Text($"{kind}の {index + 1:N0} 件目の {field.Value}") : Text($"{kind}の {index + 1:N0} 件目"));
        }
        if (ex.LineNumber is { } line)
        {
            where.Add(Text($"{line + 1:N0} 行目"));
        }
        var location = where.Count == 0 ? "" : "（" + string.Join("、", where) + "）";
        return $"JSON として読めませんでした{location}。TaskDeck で書き出したファイルか確かめてください。";
    }

    private static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>書き込む行（外部キーを満たす順に並べて渡す）。</summary>
    private sealed class Rows
    {
        public List<Project> Projects { get; } = [];
        public List<Tag> Tags { get; } = [];
        public List<RecurrenceRule> Rules { get; } = [];
        public List<TaskItem> Tasks { get; } = [];
        public List<TaskTag> TaskTags { get; } = [];
        public List<TaskTemplate> Templates { get; } = [];
        public List<TaskTemplateItem> Items { get; } = [];
        public List<string> Skipped { get; } = [];
        public int MergedProjects { get; set; }
        public int MergedTags { get; set; }

        public int Total => Projects.Count + Tags.Count + Rules.Count + Tasks.Count + TaskTags.Count + Templates.Count + Items.Count;

        /// <summary>親の表から（タスクと項目は浅いものから並べてある）。</summary>
        public IEnumerable<object> InInsertOrder() =>
            Projects.Cast<object>().Concat(Tags).Concat(Rules).Concat(Tasks).Concat(TaskTags).Concat(Templates).Concat(Items);
    }
}
