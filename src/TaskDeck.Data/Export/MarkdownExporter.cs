using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TaskDeck.Core;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.Export;

/// <summary>Markdown にした結果。TaskCount は書いたタスクの数（0 ならファイルを作らない）。</summary>
public sealed record MarkdownDocument(DateOnly From, DateOnly To, string Text, int TaskCount);

/// <summary>
/// 完了したタスクの Markdown 出力（F-108）。Obsidian のデイリーノートにそのまま貼れる形にする:
/// 完了した日（<b>ローカル日付</b>）ごとに「## 2026-09-22（火）」の見出しを立て、その下に「- [x] タイトル」を完了した順に並べる。
/// 入れるのは期間内（from〜to のローカル日付、両端を含む）に完了したタスクだけ（中止・削除済み・未完了は入れない）。
/// UTF-8（BOM なし）・改行は LF。書き込みは一時ファイルに書いてから置き換える（途中で失敗しても元のファイルを壊さない）。
/// </summary>
public sealed class MarkdownExporter(IDbContextFactory<TaskDeckDbContext> factory, IClock clock)
{
    private static readonly string[] WeekdayNames = ["日", "月", "火", "水", "木", "金", "土"];

    /// <summary>
    /// 出力するファイルの名前（TaskDeck_2026-09-20_2026-09-26.md、1日なら TaskDeck_2026-09-22.md）。
    /// デイリーノートの名前（2026-09-22.md）と重ならないよう頭に TaskDeck_ を付ける（同じフォルダに出しても上書きしない）。
    /// </summary>
    public static string SuggestFileName(DateOnly from, DateOnly to) => from == to
        ? $"TaskDeck_{Day(from)}.md"
        : $"TaskDeck_{Day(from)}_{Day(to)}.md";

    /// <summary>期間内に完了したタスクを Markdown にする（中身はスレッドプールで作る）。</summary>
    public Task<MarkdownDocument> BuildAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.Run(
            async () =>
            {
                var fromUtc = clock.LocalDayStartUtc(from);
                var toUtc = clock.LocalDayStartUtc(to.AddDays(1));
                await using var db = await factory.CreateDbContextAsync(ct);
                var rows = await db.Tasks.AsNoTracking()
                    .Where(t => t.DeletedAt == null && t.Status == TaskItemStatus.Completed
                        && t.CompletedAt != null && t.CompletedAt >= fromUtc && t.CompletedAt < toUtc)
                    .OrderBy(t => t.CompletedAt).ThenBy(t => t.Id)
                    .Select(t => new { t.Title, CompletedAt = t.CompletedAt!.Value })
                    .ToListAsync(ct);

                var text = new StringBuilder();
                foreach (var day in rows.GroupBy(r => clock.ToLocalDate(r.CompletedAt)))
                {
                    if (text.Length > 0)
                    {
                        text.Append('\n');
                    }
                    text.Append("## ").Append(Day(day.Key)).Append('（').Append(WeekdayNames[(int)day.Key.DayOfWeek]).Append("）\n\n");
                    foreach (var row in day)
                    {
                        text.Append("- [x] ").Append(OneLine(row.Title)).Append('\n');
                    }
                }
                return new MarkdownDocument(from, to, text.ToString(), rows.Count);
            },
            ct);

    /// <summary>ファイルに書く（あれば上書き）。一時ファイルに書いてから置き換える。</summary>
    public static async Task WriteAsync(string path, MarkdownDocument document, CancellationToken ct = default)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, document.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>タイトルは1行のはずだが、念のため改行を空白にする（リストの行が割れないように）。</summary>
    private static string OneLine(string title) => title.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
