using TaskDeck.Data.External;

namespace TaskDeck.Tests.Insights;

/// <summary>天気の場所の検索（Open-Meteo の geocoding）: 送るのは入力した地名だけ。選んだら緯度経度を丸めて設定に入れる。</summary>
public sealed class PlaceSearchTests : IDisposable
{
    private const string OsakaJson =
        """
        {"results":[
          {"id":1853909,"name":"大阪市","latitude":34.69374,"longitude":135.50218,"elevation":17.0,"feature_code":"PPLA","country_code":"JP","admin1":"大阪府","timezone":"Asia/Tokyo","country":"日本"},
          {"id":1853908,"name":"大阪","latitude":34.6,"longitude":135.4,"country_code":"JP","country":"日本"}
        ],"generationtime_ms":0.5}
        """;

    private readonly ExternalWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task SearchAsync_Name_SendsOnlyTheNameAndReturnsCandidates()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(OsakaJson));

        var places = await _world.NewPlaceSearch().SearchAsync("  大阪  ");

        var uri = Assert.Single(_world.Handler.Uris);
        Assert.Equal("https://geocoding-api.open-meteo.com/v1/search?name=%E5%A4%A7%E9%98%AA&count=10&language=ja&format=json", uri.AbsoluteUri);
        Assert.NotNull(places);
        Assert.Equal(2, places.Count);
        Assert.Equal("大阪市（大阪府・日本）", places[0].Label);
        Assert.Equal("大阪（日本）", places[1].Label);
    }

    [Fact]
    public void ToLocation_RoundsCoordinatesToTwoDecimals()
    {
        var location = new PlaceCandidate("大阪市", "大阪府", "日本", 34.69374, 135.50218).ToLocation();

        Assert.Equal("大阪市（大阪府・日本）", location.Name);
        Assert.Equal(34.69, location.Latitude);
        Assert.Equal(135.5, location.Longitude);
    }

    [Fact]
    public async Task SearchAsync_NoResults_ReturnsEmpty()
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json("""{"generationtime_ms":0.3}"""));

        var places = await _world.NewPlaceSearch().SearchAsync("存在しない町");

        Assert.NotNull(places);
        Assert.Empty(places);
    }

    [Fact]
    public async Task SearchAsync_Offline_ReturnsNullWithoutSending()
    {
        _world.Settings.Current.External.OfflineMode = true;

        Assert.Null(await _world.NewPlaceSearch().SearchAsync("大阪"));
        Assert.Empty(_world.Handler.Requests);
    }

    [Fact]
    public async Task SearchAsync_BlankName_SendsNothing()
    {
        var places = await _world.NewPlaceSearch().SearchAsync("   ");

        Assert.NotNull(places);
        Assert.Empty(places);
        Assert.Empty(_world.Handler.Requests);
    }

    [Theory]
    [InlineData("""{"results":"x"}""")]
    [InlineData("""{"results":[{"name":"大阪市","latitude":"34.69","longitude":135.5}]}""")]
    [InlineData("""{"results":[{"name":"どこか","latitude":134.69,"longitude":135.5}]}""")]
    public async Task SearchAsync_UnexpectedShape_ReturnsNull(string json)
    {
        _world.Handler.Respond = (_, _) => Task.FromResult(FakeExternalHandler.Json(json));

        Assert.Null(await _world.NewPlaceSearch().SearchAsync("大阪"));
    }
}
