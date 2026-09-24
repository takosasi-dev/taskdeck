using System.Globalization;
using System.Text;
using TaskDeck.Core;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Queries;
using TaskDeck.Core.Services;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.ImportExport;

/// <summary>
/// CSV エクスポート（F-106「一覧の表示内容をそのまま」。Excel で見る用）。
/// 一覧と同じく <see cref="ITaskRepository.QueryAsync"/> → <see cref="TaskTree.Arrange"/> の順で書く。
/// 条件が null（メイン画面が一覧を出していない）なら「すべて」ビューの条件で書く。
/// Excel で文字化けしないよう UTF-8（BOM 付き）・CRLF。値は RFC 4180 で囲み、先頭が = + - @ タブ CR の値には ' を付ける（CSV インジェクション対策）。
/// </summary>
public sealed class CsvExporter(ITaskRepository tasks, IProjectRepository projects, ITagRepository tags, IClock clock)
{
    public const string Header = "タイトル,階層,状態,優先度,期限,プロジェクト,タグ,メモ,作成日時,完了日時";

    private const string DateFormat = "yyyy/MM/dd";
    private const string DateTimeFormat = "yyyy/MM/dd HH:mm";

    /// <summary>書いた行数（見出しを除く）を返す。中身はスレッドプールで走らせる。</summary>
    public Task<int> ExportAsync(TaskQuery? query, Stream output, CancellationToken ct = default) =>
        Task.Run(
            async () =>
            {
                var rows = TaskTree.Arrange(await tasks.QueryAsync(query ?? BuiltInViews.QueryFor(ViewKey.All), ct));
                var projectNames = (await projects.GetAllAsync(includeArchived: true, ct)).ToDictionary(p => p.Id, p => p.Name);
                var tagNames = (await tags.GetAllAsync(ct)).ToDictionary(t => t.Id, t => t.Name);

                var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 1 << 16, leaveOpen: true)
                {
                    NewLine = "\r\n",
                };
                await using (writer)
                {
                    await writer.WriteLineAsync(Header);
                    foreach (var row in rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(Line(row, projectNames, tagNames));
                    }
                }
                return rows.Count;
            },
            ct);

    private string Line(TreeRow row, Dictionary<Guid, string> projectNames, Dictionary<Guid, string> tagNames)
    {
        var task = row.Row.Task;
        var tagText = string.Join(
            ' ',
            row.Row.TagIds.Select(id => tagNames.GetValueOrDefault(id)).OfType<string>().Order(StringComparer.OrdinalIgnoreCase));
        string?[] fields =
        [
            task.Title,
            row.Level.ToString(CultureInfo.InvariantCulture),
            StatusText(task.Status),
            PriorityText(task.Priority),
            DueText(task),
            task.ProjectId is { } projectId ? projectNames.GetValueOrDefault(projectId) : null,
            tagText,
            task.Notes,
            clock.ToLocal(task.CreatedAt).ToString(DateTimeFormat, CultureInfo.InvariantCulture),
            task.CompletedAt is { } closed ? clock.ToLocal(closed).ToString(DateTimeFormat, CultureInfo.InvariantCulture) : null,
        ];
        return string.Join(',', fields.Select(Field));
    }

    /// <summary>期限はローカル時刻で。日付のみなら日付だけ。</summary>
    private string DueText(TaskItem task) => task.DueAt switch
    {
        null => "",
        { } due when task.DueHasTime => clock.ToLocal(due).ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        { } due => clock.ToLocalDate(due).ToString(DateFormat, CultureInfo.InvariantCulture),
    };

    private static string StatusText(TaskItemStatus status) => status switch
    {
        TaskItemStatus.NotStarted => "未着手",
        TaskItemStatus.InProgress => "進行中",
        TaskItemStatus.Completed => "完了",
        TaskItemStatus.Cancelled => "中止",
        _ => status.ToString(),
    };

    /// <summary>「なし」は一覧と同じく空欄。</summary>
    private static string PriorityText(Priority priority) => priority switch
    {
        Priority.None => "",
        Priority.Low => "低",
        Priority.Medium => "中",
        Priority.High => "高",
        Priority.Urgent => "緊急",
        _ => priority.ToString(),
    };

    /// <summary>
    /// 1つの値を CSV の欄にする。先頭が = + - @ タブ CR なら ' を前に付けて式として読まれないようにし（CSV インジェクション対策）、
    /// " , CR LF を含むなら " で囲んで中の " を2つにする（RFC 4180）。
    /// </summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }
        return value.AsSpan().IndexOfAny("\",\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
