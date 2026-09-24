using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.ImportExport;

/// <summary>進み具合。Total が 0 のときは全体の量が分からない（バーを流すだけ）。Stage はそのまま画面に出せる文。</summary>
public sealed record DataTransferProgress(string Stage, int Done = 0, int Total = 0);

/// <summary>書き出した件数。タスクはゴミ箱を含む。プロジェクト・タグ・テンプレートは削除済みの名残を数えない。</summary>
public sealed record ExportSummary(int TaskCount, int ProjectCount, int TagCount, int TemplateCount);

/// <summary>
/// JSON エクスポート（F-104）。形は <see cref="ExportDocument"/>。
/// 全表を1つの読み取りトランザクションで読む（途中で書き込みが入っても、表どうしの参照が食い違ったファイルにならない）。
/// 画面を止めないよう、中身はスレッドプールで走らせる（UI スレッドからそのまま await してよい）。
/// </summary>
public sealed class JsonExporter(IDbContextFactory<TaskDeckDbContext> factory, IClock clock, ILogger<JsonExporter> logger)
{
    public Task<ExportSummary> ExportAsync(Stream output, IProgress<DataTransferProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(
            async () =>
            {
                progress?.Report(new DataTransferProgress("データを読んでいます"));
                var document = await ReadAllAsync(ct);
                progress?.Report(new DataTransferProgress("ファイルに書いています"));
                await JsonSerializer.SerializeAsync(output, document, ExportJson.Options, ct);
                await output.FlushAsync(ct);
                var summary = new ExportSummary(
                    document.Tasks.Count,
                    document.Projects.Count(p => p.DeletedAt is null),
                    document.Tags.Count(t => t.DeletedAt is null),
                    document.Templates.Count(t => t.DeletedAt is null));
                logger.LogInformation(
                    "JSON に書き出しました: タスク {Tasks} 件・プロジェクト {Projects} 件・タグ {Tags} 件・テンプレート {Templates} 件",
                    summary.TaskCount,
                    summary.ProjectCount,
                    summary.TagCount,
                    summary.TemplateCount);
                return summary;
            },
            ct);

    internal async Task<ExportDocument> ReadAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        // 書き込みを止めない読み取りだけのトランザクション（WAL なので、最初に読んだ時点の状態がそろって読める）。
        // EF の BeginTransaction は BEGIN IMMEDIATE で書き込みまで止めてしまうので、接続から deferred で始める
        await using var tx = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(tx, ct);

        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(ct);
        var rules = await db.RecurrenceRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        // 親を先に並べる（読み込みで親から入れるときに、そのままの順で使える）
        var tasks = await db.Tasks.AsNoTracking()
            .OrderBy(t => t.Depth).ThenBy(t => t.SortOrder).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var taskTags = await db.TaskTags.AsNoTracking().OrderBy(x => x.TaskId).ThenBy(x => x.TagId).ToListAsync(ct);
        var templates = await db.Templates.AsNoTracking().OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToListAsync(ct);
        var items = await db.TemplateItems.AsNoTracking()
            .OrderBy(i => i.TemplateId).ThenBy(i => i.Depth).ThenBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync(ct);

        return new ExportDocument
        {
            FormatVersion = ExportDocument.CurrentFormatVersion,
            ExportedAt = clock.UtcNow,
            AppVersion = typeof(JsonExporter).Assembly.GetName().Version?.ToString(3),
            Projects =
            [
                .. projects.Select(p => new ProjectRecord
                {
                    Id = p.Id,
                    Name = p.Name,
                    ColorHex = p.ColorHex,
                    IconKey = p.IconKey,
                    SortOrder = p.SortOrder,
                    IsArchived = p.IsArchived,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    DeletedAt = p.DeletedAt,
                }),
            ],
            Tags =
            [
                .. tags.Select(t => new TagRecord
                {
                    Id = t.Id,
                    Name = t.Name,
                    ColorHex = t.ColorHex,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt,
                    DeletedAt = t.DeletedAt,
                }),
            ],
            RecurrenceRules =
            [
                .. rules.Select(r => new RecurrenceRuleRecord
                {
                    Id = r.Id,
                    RRule = r.RRule,
                    AnchorAt = r.AnchorAt,
                    EndKind = r.EndKind,
                    EndDate = r.EndDate,
                    MaxOccurrences = r.MaxOccurrences,
                    CompletedCount = r.CompletedCount,
                    BaseKind = r.BaseKind,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                    DeletedAt = r.DeletedAt,
                }),
            ],
            Tasks =
            [
                .. tasks.Select(t => new TaskRecord
                {
                    Id = t.Id,
                    Title = t.Title,
                    Notes = t.Notes,
                    Status = t.Status,
                    Priority = t.Priority,
                    DueAt = t.DueAt,
                    DueDate = t.DueAt is { } due && !t.DueHasTime ? clock.ToLocalDate(due) : null,
                    DueHasTime = t.DueHasTime,
                    RemindAt = t.RemindAt,
                    RemindOffsetMinutes = t.RemindOffsetMinutes,
                    NotifiedAt = t.NotifiedAt,
                    CompletedAt = t.CompletedAt,
                    ParentTaskId = t.ParentTaskId,
                    Depth = t.Depth,
                    ProjectId = t.ProjectId,
                    RecurrenceRuleId = t.RecurrenceRuleId,
                    RecurrenceSeriesId = t.RecurrenceSeriesId,
                    DurationMinutes = t.DurationMinutes,
                    TemplateBatchId = t.TemplateBatchId,
                    SortOrder = t.SortOrder,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt,
                    DeletedAt = t.DeletedAt,
                }),
            ],
            TaskTags =
            [
                .. taskTags.Select(x => new TaskTagRecord
                {
                    TaskId = x.TaskId,
                    TagId = x.TagId,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt,
                    DeletedAt = x.DeletedAt,
                }),
            ],
            Templates =
            [
                .. templates.Select(t => new TemplateRecord
                {
                    Id = t.Id,
                    Name = t.Name,
                    Description = t.Description,
                    IconKey = t.IconKey,
                    ColorHex = t.ColorHex,
                    AnchorLabel = t.AnchorLabel,
                    DefaultProjectId = t.DefaultProjectId,
                    DefaultTagIds = [.. t.DefaultTagIds],
                    UseCount = t.UseCount,
                    LastUsedAt = t.LastUsedAt,
                    SortOrder = t.SortOrder,
                    CreatedAt = t.CreatedAt,
                    UpdatedAt = t.UpdatedAt,
                    DeletedAt = t.DeletedAt,
                }),
            ],
            TemplateItems =
            [
                .. items.Select(i => new TemplateItemRecord
                {
                    Id = i.Id,
                    TemplateId = i.TemplateId,
                    Title = i.Title,
                    Notes = i.Notes,
                    Priority = i.Priority,
                    DueOffsetDays = i.DueOffsetDays,
                    DueTime = i.DueTime?.ToString(ExportJson.TimeOfDayFormat, CultureInfo.InvariantCulture),
                    RemindOffsetMinutes = i.RemindOffsetMinutes,
                    DurationMinutes = i.DurationMinutes,
                    ParentItemId = i.ParentItemId,
                    Depth = i.Depth,
                    TagIds = [.. i.TagIds],
                    SortOrder = i.SortOrder,
                    CreatedAt = i.CreatedAt,
                    UpdatedAt = i.UpdatedAt,
                    DeletedAt = i.DeletedAt,
                }),
            ],
        };
    }
}
