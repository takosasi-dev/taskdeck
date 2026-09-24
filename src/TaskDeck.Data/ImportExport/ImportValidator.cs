using System.Globalization;
using System.Text.RegularExpressions;
using TaskDeck.Core;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services;

namespace TaskDeck.Data.ImportExport;

/// <summary>
/// 読み込む前の検査（NFR 6.5・設計書 3.9）。問題があれば、そのまま画面に出せる文（「タスクの 12 件目: …」）を返す。
/// ここを通ったファイルは DB の制約（必須・外部キー・3階層・タグ名の一意）に反しないので、書き込みの途中で失敗しない。
/// 文字数は、読み込みで整えた後（改行・前後の空白を除き NFKC にした後）の書記素で数える（リポジトリと同じ数え方）。
/// </summary>
internal static partial class ImportValidator
{
    private const string ProjectKind = "プロジェクト";
    private const string TagKind = "タグ";
    private const string RuleKind = "繰り返し";
    private const string TaskKind = "タスク";
    private const string TaskTagKind = "タスクとタグの対応";
    private const string TemplateKind = "テンプレート";
    private const string ItemKind = "テンプレートの項目";
    private const int IconKeyMaxLength = 50;

    private static readonly Dictionary<string, string> KindsByList = new(StringComparer.OrdinalIgnoreCase)
    {
        ["projects"] = ProjectKind,
        ["tags"] = TagKind,
        ["recurrenceRules"] = RuleKind,
        ["tasks"] = TaskKind,
        ["taskTags"] = TaskTagKind,
        ["templates"] = TemplateKind,
        ["templateItems"] = ItemKind,
    };

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorHexPattern();

    /// <summary>
    /// 問題が無ければ null。taskDepths / itemDepths には、親をたどって求めた深さ（0〜2）を入れる
    /// （ファイルの depth 列は冗長なので使わない）。
    /// </summary>
    public static string? Validate(ExportDocument doc, int maxTasks, Dictionary<Guid, int> taskDepths, Dictionary<Guid, int> itemDepths) =>
        CheckVersion(doc)
        ?? CheckCounts(doc, maxTasks)
        ?? CheckProjects(doc.Projects)
        ?? CheckTags(doc.Tags)
        ?? CheckRules(doc.RecurrenceRules)
        ?? CheckTasks(doc, taskDepths)
        ?? CheckTaskTags(doc)
        ?? CheckTemplates(doc)
        ?? CheckTemplateItems(doc, itemDepths);

    /// <summary>JSON の配列名（"tasks" など）を画面に出す呼び名に。知らない名前なら null。</summary>
    public static string? KindOf(string jsonListName) => KindsByList.GetValueOrDefault(jsonListName);

    // ---- 読み込みで使う値の整え方（検査と書き込みで同じものを使う） ----

    /// <summary>タイトル: 改行を空白にして前後を除く（TaskRepository と同じ。長さはここでは切らない）。</summary>
    public static string TitleOf(string? raw) => TextNormalizer.ForTitle(raw, int.MaxValue);

    /// <summary>プロジェクト・テンプレートの名前: NFKC・前後の空白を除く（ProjectRepository と同じ。長さは切らない）。</summary>
    public static string NameOf(string? raw) => TextNormalizer.ForName(raw);

    /// <summary>タグ名: TagRepository.NormalizeName と同じ規則（先頭の # を除く・空白は _）で、長さは切らない。</summary>
    public static string TagNameOf(string? raw) => TextNormalizer.ForName(raw).TrimStart('#').Trim().Replace(' ', '_');

    /// <summary>任意の短い文字列: 空白だけなら null、前後の空白を除く。</summary>
    public static string? Trimmed(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    /// <summary>"HH:mm"（"H:mm" も受ける）。空なら null。読めなければ false。</summary>
    public static bool TryParseTime(string? raw, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }
        if (TimeOnly.TryParseExact(raw.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            time = value;
            return true;
        }
        return false;
    }

    // ---- 検査 ----

    private static string? CheckVersion(ExportDocument doc) => doc.FormatVersion switch
    {
        ExportDocument.CurrentFormatVersion => null,
        > ExportDocument.CurrentFormatVersion =>
            Text($"新しい形式（版 {doc.FormatVersion}）のファイルです。TaskDeck を新しくしてから読み込んでください。"),
        _ => "TaskDeck で書き出したファイルではありません（formatVersion がありません）。",
    };

    private static string? CheckCounts(ExportDocument doc, int maxTasks) =>
        doc.Tasks.Count > maxTasks
            ? Text($"タスクが {doc.Tasks.Count:N0} 件あります。一度に読み込めるのは {maxTasks:N0} 件までです。")
            : null;

    private static string? CheckProjects(List<ProjectRecord> rows)
    {
        if (CheckIds(ProjectKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var error = CheckText(ProjectKind, i, "名前", NameOf(r.Name), Project.NameMaxLength)
                ?? CheckColor(ProjectKind, i, r.ColorHex)
                ?? CheckText(ProjectKind, i, "アイコンの識別子", Trimmed(r.IconKey), IconKeyMaxLength, required: false);
            if (error is not null)
            {
                return error;
            }
        }
        return null;
    }

    private static string? CheckTags(List<TagRecord> rows)
    {
        if (CheckIds(TagKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        // 削除されていないタグの名前は一意（UX_Tag_Name）。比べ方は DB と同じ NOCASE（ASCII の英字だけ大文字小文字を同じとみなす）
        var liveNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var name = TagNameOf(r.Name);
            var error = CheckText(TagKind, i, "名前", name, Tag.NameMaxLength) ?? CheckColor(TagKind, i, r.ColorHex);
            if (error is not null)
            {
                return error;
            }
            if (r.DeletedAt is null && !liveNames.TryAdd(NoCaseKey(name), i))
            {
                return At(TagKind, i, Text($"「{name}」と同じ名前のタグが {liveNames[NoCaseKey(name)] + 1:N0} 件目にもあります（英字の大文字小文字は区別しません）。"));
            }
        }
        return null;
    }

    /// <summary>SQLite の NOCASE と同じ比べ方のキー（ASCII の a〜z だけを大文字にそろえる）。</summary>
    private static string NoCaseKey(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'a' and <= 'z' ? (char)(c - ('a' - 'A')) : c;
            }
        });

    private static string? CheckRules(List<RecurrenceRuleRecord> rows)
    {
        if (CheckIds(RuleKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var error = CheckText(RuleKind, i, "規則（rrule）", r.RRule?.Trim() ?? "", RecurrenceRule.RRuleMaxLength);
            if (error is not null)
            {
                return error;
            }
            if (r.AnchorAt is null)
            {
                return At(RuleKind, i, "起点の日時（anchorAt）がありません。");
            }
            if (!Enum.IsDefined(r.EndKind))
            {
                return At(RuleKind, i, Text($"終わり方（endKind）の値（{(int)r.EndKind}）が正しくありません。"));
            }
            if (!Enum.IsDefined(r.BaseKind))
            {
                return At(RuleKind, i, Text($"数える起点（baseKind）の値（{(int)r.BaseKind}）が正しくありません。"));
            }
            if (r.CompletedCount < 0 || r.MaxOccurrences < 1)
            {
                return At(RuleKind, i, "回数（completedCount・maxOccurrences）の値が正しくありません。");
            }
        }
        return null;
    }

    private static string? CheckTasks(ExportDocument doc, Dictionary<Guid, int> depths)
    {
        var rows = doc.Tasks;
        if (CheckIds(TaskKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        var ids = rows.Select(t => t.Id).ToHashSet();
        var projects = doc.Projects.Select(p => p.Id).ToHashSet();
        var rules = doc.RecurrenceRules.Select(r => r.Id).ToHashSet();
        for (var i = 0; i < rows.Count; i++)
        {
            var t = rows[i];
            var error = CheckText(TaskKind, i, "タイトル", TitleOf(t.Title), TaskItem.TitleMaxLength);
            if (error is not null)
            {
                return error;
            }
            if (!Enum.IsDefined(t.Status))
            {
                return At(TaskKind, i, Text($"状態（status）の値（{(int)t.Status}）が正しくありません。"));
            }
            if (!Enum.IsDefined(t.Priority))
            {
                return At(TaskKind, i, Text($"優先度（priority）の値（{(int)t.Priority}）が正しくありません。"));
            }
            if ((t.Status is TaskItemStatus.Completed or TaskItemStatus.Cancelled) && t.CompletedAt is null)
            {
                return At(TaskKind, i, "完了・中止なのに、閉じた日時（completedAt）がありません。");
            }
            if (t.DurationMinutes < 0)
            {
                return At(TaskKind, i, "所要時間が負の値です。");
            }
            if (t.ProjectId is { } projectId && !projects.Contains(projectId))
            {
                return At(TaskKind, i, Text($"プロジェクト（{projectId}）がファイルにありません。"));
            }
            if (t.RecurrenceRuleId is { } ruleId && !rules.Contains(ruleId))
            {
                return At(TaskKind, i, Text($"繰り返し（{ruleId}）がファイルにありません。"));
            }
            if (t.ParentTaskId is { } parentId && !ids.Contains(parentId))
            {
                return At(TaskKind, i, Text($"親のタスク（{parentId}）がファイルにありません。"));
            }
        }
        return CheckDepths(TaskKind, rows, t => t.Id, t => t.ParentTaskId, depths, "サブタスクが3階層を超えています。");
    }

    private static string? CheckTaskTags(ExportDocument doc)
    {
        var tasks = doc.Tasks.Select(t => t.Id).ToHashSet();
        var tags = doc.Tags.Select(t => t.Id).ToHashSet();
        for (var i = 0; i < doc.TaskTags.Count; i++)
        {
            var r = doc.TaskTags[i];
            if (r is null)
            {
                return At(TaskTagKind, i, "中身がありません。");
            }
            if (!tasks.Contains(r.TaskId))
            {
                return At(TaskTagKind, i, Text($"タスク（{r.TaskId}）がファイルにありません。"));
            }
            if (!tags.Contains(r.TagId))
            {
                return At(TaskTagKind, i, Text($"タグ（{r.TagId}）がファイルにありません。"));
            }
        }
        return null;
    }

    private static string? CheckTemplates(ExportDocument doc)
    {
        var rows = doc.Templates;
        if (CheckIds(TemplateKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        var projects = doc.Projects.Select(p => p.Id).ToHashSet();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var error = CheckText(TemplateKind, i, "名前", NameOf(r.Name), TaskTemplate.NameMaxLength)
                ?? CheckText(TemplateKind, i, "説明", Trimmed(r.Description), TaskTemplate.DescriptionMaxLength, required: false)
                ?? CheckText(TemplateKind, i, "アイコンの識別子", Trimmed(r.IconKey), IconKeyMaxLength, required: false)
                ?? CheckText(TemplateKind, i, "基準日の呼び名", Trimmed(r.AnchorLabel), TaskTemplate.AnchorLabelMaxLength, required: false)
                ?? CheckColor(TemplateKind, i, r.ColorHex);
            if (error is not null)
            {
                return error;
            }
            if (r.DefaultProjectId is { } projectId && !projects.Contains(projectId))
            {
                return At(TemplateKind, i, Text($"既定のプロジェクト（{projectId}）がファイルにありません。"));
            }
            if (r.UseCount < 0)
            {
                return At(TemplateKind, i, "使った回数が負の値です。");
            }
        }
        return null;
    }

    private static string? CheckTemplateItems(ExportDocument doc, Dictionary<Guid, int> depths)
    {
        var rows = doc.TemplateItems;
        if (CheckIds(ItemKind, rows, r => r.Id) is { } idError)
        {
            return idError;
        }
        var templates = doc.Templates.Select(t => t.Id).ToHashSet();
        var templateOf = rows.ToDictionary(r => r.Id, r => r.TemplateId);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!templates.Contains(r.TemplateId))
            {
                return At(ItemKind, i, Text($"テンプレート（{r.TemplateId}）がファイルにありません。"));
            }
            var error = CheckText(ItemKind, i, "タイトル", TitleOf(r.Title), TaskItem.TitleMaxLength);
            if (error is not null)
            {
                return error;
            }
            if (!Enum.IsDefined(r.Priority))
            {
                return At(ItemKind, i, Text($"優先度（priority）の値（{(int)r.Priority}）が正しくありません。"));
            }
            if (!TryParseTime(r.DueTime, out _))
            {
                return At(ItemKind, i, Text($"時刻（{r.DueTime}）は HH:mm の形で書いてください。"));
            }
            if (r.DurationMinutes < 0)
            {
                return At(ItemKind, i, "所要時間が負の値です。");
            }
            if (r.ParentItemId is { } parentId)
            {
                if (!templateOf.TryGetValue(parentId, out var parentTemplate))
                {
                    return At(ItemKind, i, Text($"親の項目（{parentId}）がファイルにありません。"));
                }
                if (parentTemplate != r.TemplateId)
                {
                    return At(ItemKind, i, Text($"親の項目（{parentId}）が別のテンプレートにあります。"));
                }
            }
        }
        return CheckDepths(ItemKind, rows, r => r.Id, r => r.ParentItemId, depths, "項目が3階層を超えています。");
    }

    // ---- 部品 ----

    /// <summary>行が空（null）でないこと、Id があり重複しないこと。</summary>
    private static string? CheckIds<T>(string kind, List<T> rows, Func<T, Guid> idOf)
        where T : class
    {
        var seen = new HashSet<Guid>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not { } row)
            {
                return At(kind, i, "中身がありません。");
            }
            var id = idOf(row);
            if (id == Guid.Empty)
            {
                return At(kind, i, "Id がありません。");
            }
            if (!seen.Add(id))
            {
                return At(kind, i, Text($"Id（{id}）が重複しています。"));
            }
        }
        return null;
    }

    private static string? CheckText(string kind, int index, string label, string? value, int maxLength, bool required = true)
    {
        if (string.IsNullOrEmpty(value))
        {
            return required ? At(kind, index, $"{label}がありません。") : null;
        }
        return TextNormalizer.GraphemeCount(value) > maxLength
            ? At(kind, index, Text($"{label}が {maxLength:N0} 文字を超えています。"))
            : null;
    }

    private static string? CheckColor(string kind, int index, string? value) =>
        Trimmed(value) is { } color && !ColorHexPattern().IsMatch(color)
            ? At(kind, index, Text($"色（{color}）は #RRGGBB の形で書いてください。"))
            : null;

    /// <summary>
    /// 親をたどって深さを求め、3階層を超えるもの・循環するものを見つける（親が存在することは先に確かめてある）。
    /// たどるのは1行あたり3段までなので、壊れたファイルでも件数に比例する時間で終わる。
    /// </summary>
    private static string? CheckDepths<T>(
        string kind,
        List<T> rows,
        Func<T, Guid> idOf,
        Func<T, Guid?> parentOf,
        Dictionary<Guid, int> depths,
        string tooDeep)
    {
        var byId = rows.ToDictionary(idOf);
        for (var i = 0; i < rows.Count; i++)
        {
            var hops = 0;
            var row = rows[i];
            while (parentOf(row) is { } parentId)
            {
                if (++hops > TaskItem.MaxDepth)
                {
                    return At(kind, i, HasCycle(rows[i]) ? "親子の関係が循環しています。" : tooDeep);
                }
                row = byId[parentId];
            }
            depths[idOf(rows[i])] = hops;
        }
        return null;

        bool HasCycle(T start)
        {
            var seen = new HashSet<Guid>();
            var row = start;
            while (seen.Add(idOf(row)))
            {
                if (parentOf(row) is not { } parentId)
                {
                    return false;
                }
                row = byId[parentId];
            }
            return true;
        }
    }

    private static string At(string kind, int index, string reason) => Text($"{kind}の {index + 1:N0} 件目: {reason}");

    /// <summary>数の書式を PC の地域設定に左右されないようにする（1,234 の形）。</summary>
    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
