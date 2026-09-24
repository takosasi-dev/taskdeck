using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskDeck.Core;

namespace TaskDeck.Data.ImportExport;

// JSON エクスポートの形（F-104、形式の版 1）。
// 表ごとの配列で、列は設計書 3章のデータモデルの列名を camelCase にしたもの（テンプレートの既定タグ・項目のタグは GUID の配列）。
// 日時はすべて UTC の ISO 8601（末尾 Z）。列挙は名前（"Completed" など。数値でも読める）。
// 書かないもの: キャッシュ（祝日・天気・リンクのプレビュー）、端末の状態（AppState）、同期のメタ情報（SyncMeta と各行の SyncState・RemoteUpdatedAt）、検索キー。
// 削除済みの行（ゴミ箱のタスク、消したタグ・外したタグの名残など）も書く。参照を切らずに、元と同じ状態へ戻せるようにするため。
// 読み込みでは、必須の文字列が無いことを検査で見つけて理由を返すため、文字列は null を許す形で受ける。

internal sealed class ExportDocument
{
    /// <summary>この形式の版。読めるのは 1 だけ（これより新しい版は「TaskDeck を更新して」と返す）。</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }
    public DateTime ExportedAt { get; set; }
    public string? AppVersion { get; set; }
    public List<ProjectRecord> Projects { get; set; } = [];
    public List<TagRecord> Tags { get; set; } = [];
    public List<RecurrenceRuleRecord> RecurrenceRules { get; set; } = [];
    public List<TaskRecord> Tasks { get; set; } = [];
    public List<TaskTagRecord> TaskTags { get; set; } = [];
    public List<TemplateRecord> Templates { get; set; } = [];
    public List<TemplateItemRecord> TemplateItems { get; set; } = [];
}

internal sealed class ProjectRecord
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? ColorHex { get; set; }
    public string? IconKey { get; set; }
    public double SortOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>書き出すだけ。読み込んだ行の更新日時は読み込んだ時刻になる（AuditInterceptor が付ける）。</summary>
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class TagRecord
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? ColorHex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class RecurrenceRuleRecord
{
    public Guid Id { get; set; }

    /// <summary>RFC 5545 の RRULE。camelCase の規則だと "rRule" になるので、名前を決めておく。</summary>
    [JsonPropertyName("rrule")]
    public string? RRule { get; set; }
    public DateTime? AnchorAt { get; set; }
    public RecurrenceEndKind EndKind { get; set; }
    public DateOnly? EndDate { get; set; }
    public int? MaxOccurrences { get; set; }
    public int CompletedCount { get; set; }
    public RecurrenceBaseKind BaseKind { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class TaskRecord
{
    public Guid Id { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public TaskItemStatus Status { get; set; }
    public Priority Priority { get; set; }
    public DateTime? DueAt { get; set; }
    /// <summary>
    /// 日付のみの期限のローカル日付（DueHasTime=false のときだけ書く）。DueAt は書き出した PC の 0:00 を UTC にした値なので、
    /// 別のタイムゾーンで読み込んでも同じ日付になるよう、読み込みではこちらを優先する。人が読んでも日付が分かる。
    /// </summary>
    public DateOnly? DueDate { get; set; }
    public bool DueHasTime { get; set; }
    public DateTime? RemindAt { get; set; }
    public int? RemindOffsetMinutes { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? ParentTaskId { get; set; }
    /// <summary>書き出すだけ。読み込みでは親をたどって求め直す（冗長列のため）。</summary>
    public int Depth { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? RecurrenceRuleId { get; set; }
    public Guid? RecurrenceSeriesId { get; set; }
    public int? DurationMinutes { get; set; }
    public Guid? TemplateBatchId { get; set; }
    public double SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class TaskTagRecord
{
    public Guid TaskId { get; set; }
    public Guid TagId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class TemplateRecord
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? IconKey { get; set; }
    public string? ColorHex { get; set; }
    public string? AnchorLabel { get; set; }
    public Guid? DefaultProjectId { get; set; }
    public List<Guid>? DefaultTagIds { get; set; }
    public int UseCount { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public double SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal sealed class TemplateItemRecord
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public Priority Priority { get; set; }
    public int? DueOffsetDays { get; set; }
    /// <summary>"HH:mm"（設計書 3.8 と同じ）。null なら終日。</summary>
    public string? DueTime { get; set; }
    public int? RemindOffsetMinutes { get; set; }
    public int? DurationMinutes { get; set; }
    public Guid? ParentItemId { get; set; }
    /// <summary>書き出すだけ。読み込みでは親をたどって求め直す。</summary>
    public int Depth { get; set; }
    public List<Guid>? TagIds { get; set; }
    public double SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}

internal static class ExportJson
{
    public const string TimeOfDayFormat = "HH:mm";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // 日本語を \uXXXX にしない（UTF-8 のファイルとして人が読めるように）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(), new UtcIsoDateTimeConverter() },
    };
}

/// <summary>
/// 日時は UTC の ISO 8601（末尾 Z、端数の 0 は省く）で書く。
/// 読むときはオフセット付き（+09:00）も受けて UTC に直し、オフセットの無い値は UTC とみなす。
/// </summary>
internal sealed class UtcIsoDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateTime.TryParse(
                reader.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var value))
        {
            return value;
        }
        throw new JsonException("日時の形が正しくありません。");
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString(Format, CultureInfo.InvariantCulture));
}
