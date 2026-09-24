using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TaskDeck.Core.Settings;

namespace TaskDeck.Data.External;

/// <summary>地名検索の候補1件。緯度経度は検索結果のまま（選んだときに丸める）。</summary>
public sealed record PlaceCandidate(string Name, string? Region, string? Country, double Latitude, double Longitude)
{
    /// <summary>「大阪市（大阪府・日本）」の形。</summary>
    public string Label
    {
        get
        {
            var parts = new[] { Region, Country }.Where(p => !string.IsNullOrWhiteSpace(p) && p != Name).ToList();
            return parts.Count == 0 ? Name : $"{Name}（{string.Join("・", parts)}）";
        }
    }

    /// <summary>設定に入れる形（名前は「大阪市（大阪府・日本）」、緯度経度は小数第2位に丸める。NFR 6.4）。</summary>
    public WeatherLocation ToLocation() => new(Label, WeatherUpdater.Round(Latitude), WeatherUpdater.Round(Longitude));

    /// <summary>一覧の項目としての読み上げ名（UI Automation は項目の ToString を使う）。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 天気の場所を地名で探す（Open-Meteo の geocoding）。送るのは入力した地名だけ（ほかのクエリは固定の値）。
/// 結果の緯度経度は選んだときに丸めて設定に入れる。
/// </summary>
public sealed class PlaceSearch(ExternalHttp http, ILogger<PlaceSearch> logger)
{
    public const int MaxResults = 10;

    /// <summary>送る地名の長さの上限（それ以上は切る）。</summary>
    public const int MaxNameLength = 100;

    public static Uri RequestUri(string name) => new(
        "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name)
        + "&count=" + MaxResults.ToString(CultureInfo.InvariantCulture)
        + "&language=ja&format=json");

    /// <summary>
    /// 地名で探す。null は通信しなかった・できなかった（オフライン・失敗・想定外の応答）。空の一覧は見つからなかった。
    /// </summary>
    public async Task<IReadOnlyList<PlaceCandidate>?> SearchAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }
        if (trimmed.Length > MaxNameLength)
        {
            trimmed = trimmed[..MaxNameLength];
        }
        var root = await http.GetJsonAsync(ExternalApi.Geocoding, RequestUri(trimmed), ct);
        if (root is null)
        {
            return null;
        }
        var places = Parse(root.Value);
        if (places is null)
        {
            logger.LogWarning("地名検索の応答が想定外の形だったので使いませんでした");
        }
        return places;
    }

    /// <summary>{"results":[{"name":"大阪市","latitude":34.69,"longitude":135.50,"admin1":"大阪府","country":"日本"}]}。見つからなければ results が無い。</summary>
    internal static IReadOnlyList<PlaceCandidate>? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!root.TryGetProperty("results", out var results))
        {
            return [];
        }
        if (results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var places = new List<PlaceCandidate>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "name") is not { } placeName
                || !Number(item, "latitude", out var latitude) || latitude is < -90 or > 90
                || !Number(item, "longitude", out var longitude) || longitude is < -180 or > 180)
            {
                return null;
            }
            places.Add(new PlaceCandidate(placeName, Text(item, "admin1"), Text(item, "country"), latitude, longitude));
        }
        return places.Take(MaxResults).ToList();
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } text
            ? (text.Length > 100 ? text[..100] : text)
            : null;

    private static bool Number(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }
}
