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
    public void Dispose() => WeatherFeed.Latest.Clear();

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
}
