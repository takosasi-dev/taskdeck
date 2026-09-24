using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskDeck.Core.Abstractions;
using TaskDeck.Core.Services;
using TaskDeck.Data.External;
using TaskDeck.Data.Repositories;
using TaskDeck.Tests.TestSupport;

namespace TaskDeck.Tests.Insights;

/// <summary>
/// 外部API の偽物（本物の API は叩かない）。届いた要求を Requests に残し、Respond で答えを決める（既定は 200 で "[]"）。
/// </summary>
internal sealed class FakeExternalHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>要求から答えを作る。例外を投げれば通信の失敗になる。</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(Json("[]"));

    public IEnumerable<Uri> Uris => Requests.Select(r => r.RequestUri!);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    public static HttpResponseMessage Csv(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>決まった順に答える（足りなくなったら最後の答えを繰り返す）。</summary>
    public void RespondInOrder(params Func<HttpResponseMessage>[] responses)
    {
        var index = 0;
        Respond = (_, _) => Task.FromResult(responses[Math.Min(index++, responses.Length - 1)]());
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await Respond(request, cancellationToken);
    }
}

/// <summary>名前付きクライアントの代わりに、偽物のハンドラにつないだ HttpClient を渡す。</summary>
internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public List<string> Names { get; } = [];

    public HttpClient CreateClient(string name)
    {
        Names.Add(name);
        return new HttpClient(handler, disposeHandler: false);
    }
}

/// <summary>
/// 外部API のテストの土台: インメモリ DB・JST の固定時計（2026-09-23 10:00）・メモリの設定・偽物の HTTP。
/// 再試行の待ちは実際には待たず、待とうとした時間を Delays に残す。
/// </summary>
internal sealed class ExternalWorld : IDisposable
{
    public ExternalWorld()
    {
        Db = new TestDatabase(Clock);
        Factory = new FakeHttpClientFactory(Handler);
        Http = new ExternalHttp(Factory, Settings, Clock, NullLogger<ExternalHttp>.Instance)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(200),
            Delay = (span, _) =>
            {
                Delays.Add(span);
                return Task.CompletedTask;
            },
        };
        Holidays = new HolidayCache(Db, Settings);
        Weather = new WeatherCache(Db, Settings);
        State = new AppStateRepository(Db);
        Hub.Changed += (_, e) => Published.Add(e.Kinds);
    }

    public FixedClock Clock { get; } = FixedClock.AtLocal(2026, 9, 23, 10, 0);

    public FakeSettingsStore Settings { get; } = new();

    public DataChangeHub Hub { get; } = new();

    public TestDatabase Db { get; }

    public FakeExternalHandler Handler { get; } = new();

    public FakeHttpClientFactory Factory { get; }

    public ExternalHttp Http { get; }

    public HolidayCache Holidays { get; }

    public WeatherCache Weather { get; }

    public IAppStateRepository State { get; }

    public List<TimeSpan> Delays { get; } = [];

    public List<DataChangeKind> Published { get; } = [];

    public HolidayUpdater NewHolidayUpdater() =>
        new(Http, Db, Holidays, Hub, Settings, Clock, NullLogger<HolidayUpdater>.Instance);

    public WeatherUpdater NewWeatherUpdater() =>
        new(Http, Db, Weather, Hub, Settings, Clock, NullLogger<WeatherUpdater>.Instance);

    public PlaceSearch NewPlaceSearch() => new(Http, NullLogger<PlaceSearch>.Instance);

    public LinkPreviewService NewLinkPreviewService() =>
        new(Http, Db, Settings, Clock, NullLogger<LinkPreviewService>.Instance);

    public UpdateChecker NewUpdateChecker(string? repository, string currentVersion = "0.1.0") =>
        new(Http, State, Settings, Clock, NullLogger<UpdateChecker>.Instance, Version.Parse(currentVersion), repository);

    public async Task<int> CountHolidaysAsync()
    {
        await using var db = Db.CreateDbContext();
        return await db.Holidays.CountAsync();
    }

    public async Task<int> CountWeatherDaysAsync()
    {
        await using var db = Db.CreateDbContext();
        return await db.WeatherDays.CountAsync();
    }

    public void Dispose()
    {
        Db.Dispose();
        Handler.Dispose();
    }

    /// <summary>
    /// 内閣府の祝日 CSV の形（Shift_JIS・CRLF・見出し付き）。既定は2026年・2027年の7件
    /// （2026/5/6 の振替休日と 2026/9/22 の国民の休日は、本物と同じく名前が「休日」）。
    /// </summary>
    public static byte[] HolidaysCsv(string? csv = null) =>
        CodePagesEncodingProvider.Instance.GetEncoding(932)!.GetBytes(csv ??
            "国民の祝日・休日月日,国民の祝日・休日名称\r\n"
            + "2026/1/1,元日\r\n2026/1/12,成人の日\r\n2026/5/3,憲法記念日\r\n2026/5/6,休日\r\n2026/9/22,休日\r\n"
            + "2027/1/1,元日\r\n2027/1/11,成人の日\r\n");

    public const string HolidaysCsvUri = "https://www8.cao.go.jp/chosei/shukujitsu/syukujitsu.csv";

    /// <summary>Open-Meteo の予報の応答の形（9/23〜9/25 の3日。3日目は先の方で値が欠けた日）。</summary>
    public const string WeatherJson =
        """
        {"latitude":34.7,"longitude":135.5,"generationtime_ms":0.1,"utc_offset_seconds":32400,"timezone":"Asia/Tokyo","timezone_abbreviation":"GMT+9","elevation":12.0,
         "daily_units":{"time":"iso8601","weather_code":"wmo code","temperature_2m_max":"°C","temperature_2m_min":"°C","precipitation_probability_max":"%"},
         "daily":{"time":["2026-09-23","2026-09-24","2026-09-25"],
                  "weather_code":[3,61,null],
                  "temperature_2m_max":[27.4,24.1,null],
                  "temperature_2m_min":[20.2,19.8,null],
                  "precipitation_probability_max":[10,80,null]}}
        """;
}
