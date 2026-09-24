using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Settings;
using TaskDeck.Core.Time;

namespace TaskDeck.Data.External;

public enum UpdateState
{
    /// <summary>公開先のリポジトリが決まっていない（確認しない）。</summary>
    NotConfigured,

    /// <summary>設定で OFF。</summary>
    Disabled,

    /// <summary>オフラインモード。</summary>
    Offline,

    /// <summary>まだ確かめられていない（初回の前・取れなかった）。</summary>
    Unknown,

    UpToDate,

    /// <summary>新しい版がある。</summary>
    Available,
}

/// <summary>更新の確認の結果。LatestVersion は GitHub のタグ（"v0.2.0"）、CheckedAtUtc は最後に確かめた日時。</summary>
public sealed record UpdateStatus(UpdateState State, string? LatestVersion, DateTime? CheckedAtUtc);

/// <summary>
/// 更新の確認（F-198、GitHub Releases。設計書 4.7.7）。1日1回まで。送るのはリポジトリ名だけ。自動でダウンロードしない
/// （知らせるのは設定画面の「TaskDeck について」だけ。ダイアログもバッジも出さない）。
/// 見るのは <see cref="Repository"/> の最新のリリース（GitHub の /releases/latest はプレリリースを返さないので、正式版だけを知らせる）。
/// リポジトリ名が null の間（テスト）は通信しない。最後に確かめた日時は AppStateKeys.LastUpdateCheck、見つけた最新のタグは AppStateKeys.LatestKnownVersion に持つ。
/// </summary>
public sealed partial class UpdateChecker(
    ExternalHttp http,
    IAppStateRepository state,
    ISettingsStore settings,
    IClock clock,
    ILogger<UpdateChecker> logger,
    Version currentVersion,
    string? repository)
{
    /// <summary>公開先（GitHub の "owner/name"）。</summary>
    public const string Repository = "takosasi-dev/taskdeck";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>今の版（3桁）。</summary>
    public string CurrentVersion { get; } = Normalize(currentVersion).ToString(3);

    /// <summary>リリースの一覧を開く先。リポジトリが決まっていなければ null。</summary>
    public string? ReleasesPage => repository is null ? null : $"https://github.com/{repository}/releases/latest";

    public static Uri RequestUri(string repository) => new($"https://api.github.com/repos/{repository}/releases/latest");

    /// <summary>通信せずに、覚えている結果から今の状態を返す。</summary>
    public async Task<UpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var (checkedAt, latest) = await ReadAsync(ct);
        return Status(checkedAt, latest);
    }

    /// <summary>前回から1日たっていれば（force なら必ず）確かめる。リポジトリが null・設定が OFF・オフラインなら通信しない。</summary>
    public async Task<UpdateStatus> CheckAsync(bool force, CancellationToken ct = default)
    {
        var (checkedAt, latest) = await ReadAsync(ct);
        var external = settings.Current.External;
        if (repository is null || !external.UpdateCheckEnabled || external.OfflineMode
            || (!force && checkedAt is { } last && clock.UtcNow - last < Interval))
        {
            return Status(checkedAt, latest);
        }

        var root = await http.GetJsonAsync(ExternalApi.Releases, RequestUri(repository), ct);
        if (root is null)
        {
            return Status(checkedAt, latest);
        }
        if (ParseTag(root.Value) is not { } tag)
        {
            logger.LogWarning("更新の確認の応答が想定外の形だったので使いませんでした");
            return Status(checkedAt, latest);
        }
        checkedAt = clock.UtcNow;
        await state.SetAsync(AppStateKeys.LastUpdateCheck, checkedAt.Value.ToString("O", CultureInfo.InvariantCulture), ct);
        await state.SetAsync(AppStateKeys.LatestKnownVersion, tag, ct);
        logger.LogInformation("更新を確認しました（最新 {Latest}、この版 {Current}）", tag, CurrentVersion);
        return Status(checkedAt, tag);
    }

    /// <summary>"v1.2.3"・"1.2.3"・"v1.2.3-beta.1" の数字の部分。読めなければ null。</summary>
    internal static Version? ParseVersion(string? tag)
    {
        var match = tag is null ? null : VersionPattern().Match(tag);
        return match is { Success: true } && Version.TryParse(match.Groups[1].Value, out var version) ? Normalize(version) : null;
    }

    /// <summary>{"tag_name":"v0.2.0",...}。タグが無い・読めなければ null。</summary>
    internal static string? ParseTag(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
        && tag.GetString() is { Length: > 0 and <= 50 } text && ParseVersion(text) is not null
            ? text
            : null;

    private UpdateStatus Status(DateTime? checkedAt, string? latest)
    {
        var external = settings.Current.External;
        if (repository is null)
        {
            return new UpdateStatus(UpdateState.NotConfigured, null, null);
        }
        if (!external.UpdateCheckEnabled)
        {
            return new UpdateStatus(UpdateState.Disabled, latest, checkedAt);
        }
        if (external.OfflineMode)
        {
            return new UpdateStatus(UpdateState.Offline, latest, checkedAt);
        }
        if (ParseVersion(latest) is not { } latestVersion)
        {
            return new UpdateStatus(UpdateState.Unknown, null, checkedAt);
        }
        var result = latestVersion > Normalize(currentVersion) ? UpdateState.Available : UpdateState.UpToDate;
        return new UpdateStatus(result, latest, checkedAt);
    }

    private async Task<(DateTime? CheckedAt, string? Latest)> ReadAsync(CancellationToken ct)
    {
        var checkedText = await state.GetAsync(AppStateKeys.LastUpdateCheck, ct);
        DateTime? checkedAt = DateTime.TryParse(checkedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
        return (checkedAt, await state.GetAsync(AppStateKeys.LatestKnownVersion, ct));
    }

    /// <summary>3桁にそろえる（アセンブリの 0.1.0.0 と タグの 0.1.0 を同じ版として比べるため）。</summary>
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    [GeneratedRegex(@"^v?(\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
