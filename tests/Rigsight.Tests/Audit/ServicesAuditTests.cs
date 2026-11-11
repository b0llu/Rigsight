using System.Net;
using System.Net.Http;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Audit;

/// <summary>
/// The audit of the unreleased widgets' outside services and platform parts (2026-11-11): the weather (MET Norway and
/// OpenStreetMap's Nominatim, read from fixed answers, never the internet), shortcuts opened from the agent, and the Media
/// tile's player names. Each test failed before its fix.
/// </summary>
[Collection("Drawing")]
public class ServicesAuditTests : IDisposable
{
    private readonly HttpClient _http = WeatherFeed.Http;

    public void Dispose()
    {
        WeatherFeed.Http = _http;
        WeatherFeed.Latest.Clear();
    }

    /// <summary>Answers every request with <paramref name="answer"/>, and keeps the requests.</summary>
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Ask, DateTime At)> Asks { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Asks) Asks.Add((request, DateTime.UtcNow));
            return Task.FromResult(answer(request));
        }
    }

    private static Stub Serve(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var stub = new Stub(answer);
        WeatherFeed.Http = WeatherFeed.NewClient(stub);
        return stub;
    }

    private const string Forecast = """
        {"properties":{"timeseries":[
          {"time":"2026-11-11T03:00:00Z","data":{"instant":{"details":{"air_temperature":4.5}},"next_1_hours":{"summary":{"symbol_code":"cloudy"}}}}
        ]}}
        """;

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };

    // ── An answer in a shape that wasn't expected ─────────────────────────

    [Fact]
    public void A_place_search_answer_that_isnt_a_list_finds_nothing_instead_of_throwing()
    {
        // Nominatim answers some failures with an object, not a list; it threw InvalidOperationException, which the
        // editor's Find button turned into the app's "Something went wrong" box.
        Assert.Empty(WeatherFeed.ParsePlaces("""{"error":"Unable to geocode"}"""));
        Assert.Empty(WeatherFeed.ParsePlaces("not json"));
        // Latitude and longitude as numbers, not strings, are still read.
        var places = WeatherFeed.ParsePlaces("""[{"lat":59.91,"lon":10.75,"name":"Oslo","display_name":"Oslo, Norway"}]""");
        Assert.Equal(new WeatherPlace("Oslo", "Norway", 59.91, 10.75), Assert.Single(places));
    }

    [Fact]
    public void A_forecast_in_an_unexpected_shape_is_no_weather_instead_of_throwing()
    {
        var now = new DateTime(2026, 11, 11, 9, 0, 0);
        Assert.Null(WeatherFeed.Parse("""{"properties":{"timeseries":{}}}""", now));
        Assert.Null(WeatherFeed.Parse("""{"properties":{"timeseries":[1,2]}}""", now));
        Assert.Null(WeatherFeed.Parse("<html>", now));
        // A step without a temperature is skipped, not the whole forecast.
        var w = WeatherFeed.Parse("""
            {"properties":{"timeseries":[
              {"time":"2026-11-11T03:00:00Z","data":{"instant":{"details":{"air_temperature":null}}}},
              {"time":"2026-11-11T04:00:00Z","data":{"instant":{"details":{"air_temperature":5.5}},"next_1_hours":{"summary":{"symbol_code":7}}}},
              {"time":"2026-11-11T05:00:00Z","data":{"instant":{"details":{"air_temperature":6.0}},"next_1_hours":{"summary":{"symbol_code":"rain"}}}}
            ]}}
            """, now);
        Assert.NotNull(w);
        Assert.Equal(5.5, w.TempC);
    }

    // ── MET Norway's terms: Expires, If-Modified-Since, and its answers that aren't a forecast ──────────────

    [Fact]
    public async Task A_forecast_asked_for_again_says_when_it_was_last_changed_and_keeps_it_when_unchanged()
    {
        var changed = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var stub = Serve(ask =>
        {
            if (ask.Headers.IfModifiedSince is null)
            {
                var first = Json(Forecast);
                first.Content.Headers.LastModified = changed;
                first.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
                return first;
            }
            return new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };
        });
        var w = await WeatherFeed.GetAsync(59.91, 10.75);
        Assert.NotNull(w);
        Assert.Equal(changed, w.LastModified);
        Assert.NotNull(w.Expires);
        WeatherFeed.Latest[WeatherFeed.Key(59.91, 10.75)] = w with { Fetched = DateTime.Now.AddHours(-1) };

        var again = await WeatherFeed.GetAsync(59.91, 10.75);
        Assert.Equal(changed, stub.Asks[1].Ask.Headers.IfModifiedSince);
        // 304 Not Modified: the forecast already had is kept, as read now.
        Assert.NotNull(again);
        Assert.Equal(4.5, again.TempC);
        Assert.True(DateTime.Now - again.Fetched < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_forecast_isnt_asked_for_again_before_it_expires()
    {
        var stub = Serve(_ => Json(Forecast));
        var tile = new WidgetTile { Kind = WidgetTiles.Weather, Lat = 48.85, Lon = 2.35 };
        // Older than half an hour, but MET Norway said it holds for 20 minutes more.
        WeatherFeed.Latest[WeatherFeed.Key(48.85, 2.35)] = new WeatherNow(3, "cloudy", null, null, DateTime.Now.AddMinutes(-40))
        {
            Expires = DateTimeOffset.UtcNow.AddMinutes(20),
        };
        Assert.Equal(0, WeatherFeed.RefreshInBackground([tile]));
        WeatherFeed.Latest[WeatherFeed.Key(48.85, 2.35)] = WeatherFeed.Latest[WeatherFeed.Key(48.85, 2.35)] with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) };
        Assert.Equal(1, WeatherFeed.RefreshInBackground([tile]));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_forecast_is_no_weather(HttpStatusCode code)
    {
        Serve(_ => Json("<html>no</html>", code));
        Assert.Null(await WeatherFeed.GetAsync(1.5, 2.5));
    }

    [Fact]
    public async Task A_deprecated_forecast_is_still_read()
    {
        Serve(_ => Json(Forecast, HttpStatusCode.NonAuthoritativeInformation));
        Assert.Equal(4.5, (await WeatherFeed.GetAsync(1.5, 2.5))?.TempC);
    }

    [Fact]
    public async Task The_log_never_says_where_a_weather_tile_is()
    {
        // The log is copied into problem reports; a place to two decimals is about a kilometre: someone's home town.
        Serve(_ => Json(Forecast));
        Assert.NotNull(await WeatherFeed.GetAsync(-33.87, 151.21));
        Serve(_ => Json("", HttpStatusCode.TooManyRequests));
        Assert.Null(await WeatherFeed.GetAsync(-33.87, 151.21));
        Serve(_ => throw new HttpRequestException("No such host is known."));
        Assert.Null(await WeatherFeed.GetAsync(-33.87, 151.21));
        string log = File.ReadAllText(Rigsight.Core.RigsightPaths.LogFile);
        Assert.Contains("[weather]", log);
        Assert.DoesNotContain("-33.87", log);
        Assert.DoesNotContain("151.21", log);
    }

    // ── Nominatim's policy: one search a second at most, and the same search not sent twice ─────────────────

    [Fact]
    public async Task Place_searches_are_a_second_apart_and_the_same_one_isnt_sent_twice()
    {
        var stub = Serve(_ => Json("""[{"lat":"59.91","lon":"10.75","name":"Oslo","display_name":"Oslo, Norway"}]"""));
        string a = "Oslo " + Guid.NewGuid().ToString("N")[..6], b = "Bergen " + Guid.NewGuid().ToString("N")[..6];
        Assert.Single(await WeatherFeed.FindAsync(a));
        Assert.Single(await WeatherFeed.FindAsync(b));
        Assert.Single(await WeatherFeed.FindAsync(" " + a.ToUpperInvariant() + " "));
        Assert.Equal(2, stub.Asks.Count);
        Assert.True(stub.Asks[1].At - stub.Asks[0].At >= TimeSpan.FromMilliseconds(950), $"{(stub.Asks[1].At - stub.Asks[0].At).TotalMilliseconds} ms apart");
    }

    [Fact]
    public async Task A_refused_place_search_finds_nothing_and_isnt_remembered()
    {
        var stub = Serve(_ => Json("<html>blocked</html>", HttpStatusCode.Forbidden));
        string q = "Lima " + Guid.NewGuid().ToString("N")[..6];
        Assert.Empty(await WeatherFeed.FindAsync(q));
        Assert.Empty(await WeatherFeed.FindAsync(q));
        Assert.Equal(2, stub.Asks.Count);
    }
}
