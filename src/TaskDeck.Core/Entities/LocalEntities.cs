namespace TaskDeck.Core.Entities;

/// <summary>同期メタ情報（単一行、Id=1）。Phase 5 まで使わないが表は作っておく。</summary>
public sealed class SyncMeta
{
    public int Id { get; set; } = 1;
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    public DateTime? LastSyncAt { get; set; }
    public string? LastSyncCursor { get; set; }
    public string? UserId { get; set; }
}

/// <summary>端末ローカルの状態（キーと値）。同期しない。キーは AppStateKeys。</summary>
public sealed class AppStateEntry
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>祝日のキャッシュ（内閣府「国民の祝日」CSV）。同期しない。</summary>
public sealed class Holiday
{
    public DateOnly Date { get; set; }
    public string LocalName { get; set; } = "";
    public string CountryCode { get; set; } = "JP";
}

/// <summary>天気のキャッシュ（Open-Meteo の日別予報）。同期しない。場所を変えたら全削除する。</summary>
public sealed class WeatherDay
{
    public DateOnly Date { get; set; }
    /// <summary>WMO の天気コード。</summary>
    public int WeatherCode { get; set; }
    public double TemperatureMax { get; set; }
    public double TemperatureMin { get; set; }
    public int? PrecipitationProbability { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>URL のタイトルのキャッシュ（Microlink、既定 OFF）。30日で取り直す。同期しない。</summary>
public sealed class LinkPreview
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public DateTime FetchedAt { get; set; }
}
