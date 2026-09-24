using System.Text;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Entities;
using TaskDeck.Core.Services.QuickInput;
using TaskDeck.Core.Services.Recurrence;
using TaskDeck.Core.Time;

namespace TaskDeck.Core.Services;

public enum ParsedTokenKind
{
    Date,
    Time,
    Tag,
    Project,
    Priority,
    Recurrence,
}

/// <summary>入力文字列の中で解釈に使った部分（Start と Length は入力文字列の添字）。チップ表示と強調に使う。</summary>
public sealed record ParsedToken(ParsedTokenKind Kind, int Start, int Length, string Text);

/// <summary>解釈できなかった記法（「!最高」など）。タスクは作られ、その部分はタイトルに残る。</summary>
public sealed record ParseWarning(string Token, string Message);

/// <summary>
/// クイック入力の解釈結果（設計書 4.2）。DueAt は UTC（日付のみならローカル 0:00 の UTC）。
/// RRule は RFC 5545。繰り返しだけ指定されて日付が無いときは、最初の発生日を DueAt に入れる。
/// </summary>
public sealed record ParsedTaskInput(
    string Title,
    DateTime? DueAt,
    bool DueHasTime,
    Priority Priority,
    string? ProjectName,
    IReadOnlyList<string> TagNames,
    string? RRule,
    IReadOnlyList<ParseWarning> Warnings,
    IReadOnlyList<ParsedToken> Tokens)
{
    public NewTaskRequest ToRequest() => new()
    {
        Title = Title,
        DueAt = DueAt,
        DueHasTime = DueHasTime,
        Priority = Priority,
        ProjectName = ProjectName,
        TagNames = TagNames,
        Recurrence = RRule is null ? null : new RecurrenceInput(RRule),
    };
}

/// <summary>
/// 1行の入力をタスクの項目に分解する（設計書 4.2、要件 3.7）。
/// <b>例外を投げない。</b>解釈できない部分はタイトルに残す（入力を捨てない・要件 4.5 / UX原則1）。
///
/// 決めごと:
/// - 接頭辞（<c>*</c> <c>!</c> <c>@</c> <c>#</c>）は<b>行頭か空白の直後</b>だけで効く（「C#の勉強」をタグにしないため）。
///   全角（＊！＠＃）も同じ。<c>\#</c> のように <c>\</c> を前に付けると記号のままタイトルに残る
/// - 優先度・プロジェクトが複数あれば<b>最後</b>を採る。タグは全部。<c>!1</c>=低 〜 <c>!4</c>=緊急、<c>!!!</c> は感嘆符の数
/// - 日付・時刻は<b>いちばん後ろ</b>の読めた表現を採る。直後の「に」「の」「まで」「中」はいっしょに取り除く
/// - 日付が無く時刻だけなら今日（その時刻を過ぎていれば翌日）。日付が無く繰り返しだけなら、今日以降の最初の発生日
/// - タイトルは、解釈に使った部分を元の入力から取り除いたもの（空になったら入力全体）。500文字（書記素）まで
/// </summary>
public sealed class QuickInputParser(IClock clock)
{
    private static readonly ParsedTaskInput Empty = new("", null, false, Priority.None, null, [], null, [], []);

    public ParsedTaskInput Parse(string? input)
    {
        var original = input ?? "";
        if (string.IsNullOrWhiteSpace(original))
        {
            return Empty;
        }

        var normalized = NormalizedInput.Create(original);
        var text = normalized.Text;
        var consumed = new bool[text.Length];
        var spans = new List<(ParsedTokenKind Kind, int Start, int Length)>();
        var warnings = new List<ParseWarning>();
        var tags = new List<string>();
        string? projectName = null;
        string? rrule = null;
        var priority = Priority.None;

        // 1〜5: 接頭辞の付くトークン（* ! @ #）を剥がす
        for (var i = 0; i < text.Length; i++)
        {
            if (normalized.IsEscaped[i] || text[i] is not ('*' or '!' or '@' or '#') || (i > 0 && !char.IsWhiteSpace(text[i - 1])))
            {
                continue;
            }
            var end = i + 1;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }
            var token = text[i..end];
            var body = token[1..];
            var used = false;
            switch (token[0])
            {
                case '#':
                    var tag = TextNormalizer.ForName(body);
                    if (tag.Length > 0)
                    {
                        if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        {
                            tags.Add(tag);
                        }
                        used = true;
                    }
                    break;
                case '@':
                    var project = TextNormalizer.ForName(body);
                    if (project.Length > 0)
                    {
                        projectName = project;
                        used = true;
                    }
                    break;
                case '!':
                    if (ParsePriority(token) is { } parsed)
                    {
                        priority = parsed;
                        used = true;
                    }
                    else
                    {
                        warnings.Add(new ParseWarning(OriginalText(original, normalized, i, end - i), "優先度として読めませんでした"));
                    }
                    break;
                default:
                    if (RecurrenceWords.ToRRule(body) is { } recurrence)
                    {
                        rrule = recurrence;
                        used = true;
                    }
                    else
                    {
                        warnings.Add(new ParseWarning(OriginalText(original, normalized, i, end - i), "繰り返しとして読めませんでした"));
                    }
                    break;
            }
            if (used)
            {
                spans.Add((KindOf(token[0]), i, end - i));
                Consume(consumed, i, end - i);
            }
            i = end - 1;
        }

        // 6〜7: 残りを後ろから見て時刻 → 日付の順に剥がす
        var today = clock.LocalToday();
        var time = DateTimeWords.FindLastTime(Mask(text, consumed));
        if (time is { } found)
        {
            spans.Add((ParsedTokenKind.Time, found.Start, found.Length));
            Consume(consumed, found.Start, found.Length);
        }
        var date = DateTimeWords.FindLastDate(Mask(text, consumed), today);
        if (date is { } day)
        {
            spans.Add((ParsedTokenKind.Date, day.Start, day.Length));
            Consume(consumed, day.Start, day.Length);
        }

        // 8〜9: 残りをタイトルに（エスケープの \ は外す）
        var title = BuildTitle(original, normalized, spans);
        if (title.Length == 0)
        {
            title = TextNormalizer.ForTitle(original, TaskItem.TitleMaxLength);
        }

        var (dueAt, dueHasTime) = ResolveDue(date?.Value, time?.Value, rrule, today);
        var tokens = spans
            .OrderBy(s => s.Start)
            .Select(s =>
            {
                var (start, length) = normalized.ToOriginal(s.Start, s.Length);
                return new ParsedToken(s.Kind, start, length, original.Substring(start, length));
            })
            .ToList();

        return new ParsedTaskInput(title, dueAt, dueHasTime, priority, projectName, tags, rrule, warnings, tokens);
    }

    private (DateTime? DueAt, bool HasTime) ResolveDue(DateOnly? date, TimeOnly? time, string? rrule, DateOnly today)
    {
        if (date is { } day)
        {
            return time is { } at ? (clock.LocalToUtc(day, at), true) : (clock.LocalDayStartUtc(day), false);
        }

        if (rrule is not null)
        {
            // 日付が無く繰り返しだけなら、今日以降（時刻ありなら今より後）の最初の発生日を期限にする
            var hasTime = time is not null;
            var baseLocal = today.ToDateTime(time ?? TimeOnly.MinValue);
            var now = clock.LocalNow();
            foreach (var occurrence in RruleEvaluator.From(rrule, baseLocal, hasTime))
            {
                if (hasTime && occurrence < now)
                {
                    continue;
                }
                return hasTime
                    ? (clock.ToUtc(occurrence), true)
                    : (clock.LocalDayStartUtc(DateOnly.FromDateTime(occurrence)), false);
            }
            return (null, false);
        }

        if (time is { } only)
        {
            // 時刻だけなら今日。もう過ぎていれば翌日（打った瞬間に期限切れのタスクを作らない）
            var utc = clock.LocalToUtc(today, only);
            return (utc <= clock.UtcNow ? clock.LocalToUtc(today.AddDays(1), only) : utc, true);
        }
        return (null, false);
    }

    /// <summary>「!高」「!3」「!!!」を優先度に。読めなければ null（警告を出してタイトルに残す）。</summary>
    private static Priority? ParsePriority(string token)
    {
        if (token.All(c => c == '!'))
        {
            return token.Length <= 4 ? (Priority)token.Length : null;
        }
        return token[1..] switch
        {
            "緊急" => Priority.Urgent,
            "高" => Priority.High,
            "中" => Priority.Medium,
            "低" => Priority.Low,
            "1" => Priority.Low,
            "2" => Priority.Medium,
            "3" => Priority.High,
            "4" => Priority.Urgent,
            _ => null,
        };
    }

    /// <summary>解釈に使った部分とエスケープの <c>\</c> を元の入力から取り除き、空白の連続を1つに詰める。</summary>
    private static string BuildTitle(string original, NormalizedInput normalized, List<(ParsedTokenKind Kind, int Start, int Length)> spans)
    {
        var removed = new bool[original.Length];
        foreach (var span in spans)
        {
            var (start, length) = normalized.ToOriginal(span.Start, span.Length);
            for (var i = start; i < start + length; i++)
            {
                removed[i] = true;
            }
        }
        foreach (var index in normalized.EscapeBackslashes)
        {
            removed[index] = true;
        }

        var kept = new StringBuilder(original.Length);
        for (var i = 0; i < original.Length; i++)
        {
            if (!removed[i])
            {
                kept.Append(original[i]);
            }
        }

        // 空白が2つ以上続いたら1つに詰める（トークンを抜いた跡で隙間が開かないように）。1つだけならそのまま（全角空白を残す）
        var rest = kept.ToString();
        var sb = new StringBuilder(rest.Length);
        for (var i = 0; i < rest.Length;)
        {
            if (!char.IsWhiteSpace(rest[i]))
            {
                sb.Append(rest[i]);
                i++;
                continue;
            }
            var run = i;
            while (run < rest.Length && char.IsWhiteSpace(rest[run]))
            {
                run++;
            }
            sb.Append(run - i == 1 ? rest[i] : ' ');
            i = run;
        }
        return TextNormalizer.ForTitle(sb.ToString(), TaskItem.TitleMaxLength);
    }

    private static string OriginalText(string original, NormalizedInput normalized, int start, int length)
    {
        var (from, count) = normalized.ToOriginal(start, length);
        return original.Substring(from, count);
    }

    private static ParsedTokenKind KindOf(char prefix) => prefix switch
    {
        '#' => ParsedTokenKind.Tag,
        '@' => ParsedTokenKind.Project,
        '!' => ParsedTokenKind.Priority,
        _ => ParsedTokenKind.Recurrence,
    };

    private static void Consume(bool[] consumed, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            consumed[i] = true;
        }
    }

    /// <summary>解釈済みの部分を空白に置き換える（位置がずれないように長さは変えない）。</summary>
    private static string Mask(string text, bool[] consumed)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (consumed[i])
            {
                chars[i] = ' ';
            }
        }
        return new string(chars);
    }
}
