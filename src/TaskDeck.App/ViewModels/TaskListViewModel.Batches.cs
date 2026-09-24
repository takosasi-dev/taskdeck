using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;

namespace TaskDeck.App.ViewModels;

/// <summary>
/// テンプレートから一度に作った一群（F-15B、波2-E）: 見出しに出すテンプレートの名前と、見出しからのまとめて完了・削除（取り消しは1段）。
/// </summary>
public sealed partial class TaskListViewModel
{
    private static readonly IReadOnlyDictionary<Guid, string> NoBatchNames = new Dictionary<Guid, string>();

    /// <summary>一群 → テンプレートの名前（一度当てたものは覚えておく。テンプレートが変わったら忘れる）。</summary>
    private readonly Dictionary<Guid, string> _batchNames = [];

    /// <summary>テンプレートごとの項目のタイトル（一群を当てるため。テンプレートが変わったら読み直す）。</summary>
    private IReadOnlyList<TemplateTitles>? _templateTitles;

    /// <summary>テンプレートの名前と項目のタイトル（比べやすいよう正規化したもの）。</summary>
    internal sealed record TemplateTitles(string Name, DateTime? LastUsedAt, IReadOnlySet<string> Titles);

    /// <summary>テンプレートが変わった（作った・名前を変えた・消した）。見出しの名前を当て直す。</summary>
    public void InvalidateTemplates()
    {
        _templateTitles = null;
        _batchNames.Clear();
    }

    /// <summary>見出しの「まとめて完了」: 見出しの下の一群の未完了を完了にする（未完了の子がいれば確認 F-024）。取り消しは1段。</summary>
    public async Task CompleteBatchAsync(TemplateBatchHeaderViewModel header)
    {
        var rows = BatchRows(header).Where(r => r is { IsClosed: false, IsInTrash: false }).ToList();
        if (rows.Count == 0)
        {
            return;
        }
        IReadOnlyList<Guid> ids = [.. rows.Select(r => r.Id)];
        var parents = rows.Where(r => r.HasOpenSubtasks).Select(r => r.Id).ToList();
        if (parents.Count > 0)
        {
            if (await SubtaskCompletion.ResolveAsync(_tasks, ids, parents, $"「{header.Name}」の一群", Confirm) is not { } resolved)
            {
                return;
            }
            ids = resolved;
        }
        var result = await _tasks.SetCompletedAsync(ids, true);
        _undo.Record(result, $"「{header.Name}」の {rows.Count} 件を完了にしました", UndoKind.Bulk, showToast: true);
        if (LeavesView(completed: true))
        {
            foreach (var row in rows.Where(r => r.Level == 0))
            {
                row.IsFading = true;
            }
            ScheduleReload(CompleteFadeMs);
        }
    }

    /// <summary>見出しの「まとめて削除」: 見出しの下の一群をゴミ箱へ移す。取り消しは1段（トーストの「元に戻す」で全部戻る）。</summary>
    public async Task DeleteBatchAsync(TemplateBatchHeaderViewModel header)
    {
        var ids = BatchRows(header).Where(r => !r.IsInTrash).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        if (SelectedRow is { } primary && ids.Contains(primary.Id))
        {
            RememberRemoval(primary);
        }
        var result = await _tasks.SoftDeleteAsync(ids);
        _undo.Record(result, $"「{header.Name}」の {ids.Count} 件を削除しました", UndoKind.Bulk, showToast: true);
    }

    /// <summary>
    /// 一覧に2件以上ある一群の名前（テンプレートを読むのは、そういう一群があって名前がまだ分からないときだけ）。
    /// ponytail: タスクはどのテンプレートから作ったかを持たない（スキーマに列が無い）ので、一群のタイトルとテンプレートの項目のタイトルが
    /// いちばん多く一致したテンプレートの名前にする（同数なら最近使った方）。作った後に名前を変えたタスクが多いと外れ、「テンプレート」と出る。
    /// 展開したときにテンプレートの Id を残す列が入ったら、それで引く方式に上げる。
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> BatchNamesAsync(IReadOnlyList<TaskListRow> rows)
    {
        Dictionary<Guid, List<string>>? batches = null;
        foreach (var row in rows)
        {
            if (row.Task.TemplateBatchId is not { } batchId)
            {
                continue;
            }
            batches ??= [];
            if (!batches.TryGetValue(batchId, out var titles))
            {
                titles = [];
                batches[batchId] = titles;
            }
            titles.Add(row.Task.Title);
        }
        if (batches is null)
        {
            return NoBatchNames;
        }
        var unknown = batches.Where(b => b.Value.Count >= 2 && !_batchNames.ContainsKey(b.Key)).ToList();
        if (unknown.Count > 0)
        {
            _templateTitles ??= await LoadTemplateTitlesAsync();
            foreach (var (batchId, titles) in unknown)
            {
                if (MatchTemplate(titles, _templateTitles) is { } name)
                {
                    _batchNames[batchId] = name;
                }
            }
        }
        // 行はスレッドプールで組み立てるので、覚えている表そのものは渡さない
        return new Dictionary<Guid, string>(_batchNames);
    }

    private async Task<IReadOnlyList<TemplateTitles>> LoadTemplateTitlesAsync()
    {
        var result = new List<TemplateTitles>();
        foreach (var summary in await _templateRepository.GetAllAsync())
        {
            if (await _templateRepository.GetWithItemsAsync(summary.Template.Id) is { } template)
            {
                result.Add(new TemplateTitles(
                    summary.Template.Name,
                    summary.Template.LastUsedAt,
                    template.Items.Select(i => TextNormalizer.ForSearch(i.Title)).ToHashSet()));
            }
        }
        return result;
    }

    /// <summary>タイトルがいちばん多く一致するテンプレートの名前（同数なら最近使った方）。1つも一致しなければ null。</summary>
    internal static string? MatchTemplate(IEnumerable<string> titles, IReadOnlyList<TemplateTitles> templates)
    {
        var keys = titles.Select(TextNormalizer.ForSearch).ToList();
        return templates
            .Select(t => (t.Name, t.LastUsedAt, Score: keys.Count(t.Titles.Contains)))
            .Where(t => t.Score > 0)
            .OrderByDescending(t => t.Score)
            .ThenByDescending(t => t.LastUsedAt)
            .Select(t => t.Name)
            .FirstOrDefault();
    }

    /// <summary>見出しの下にある一群の行（次の見出し・入力行の手前まで。一群の Id を持つ行だけ。後から足した子は含めない）。</summary>
    private IEnumerable<TaskRowViewModel> BatchRows(TemplateBatchHeaderViewModel header)
    {
        var start = Rows.IndexOf(header);
        if (start < 0)
        {
            yield break;
        }
        for (var i = start + 1; i < Rows.Count && Rows[i] is TaskRowViewModel row; i++)
        {
            if (row.Task.TemplateBatchId == header.BatchId)
            {
                yield return row;
            }
        }
    }
}
