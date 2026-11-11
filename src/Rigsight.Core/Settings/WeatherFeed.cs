using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Rigsight.Core.Updates;

namespace Rigsight.Core.Settings;

/// <summary>A place found by name, for a weather tile: its short name and where it is.</summary>
public sealed record WeatherPlace(string Name, string Detail, double Lat, double Lon);

/// <summary>The weather at a place now: its temperature, MET Norway's symbol for it, and today's high and low.</summary>
public sealed record WeatherNow(double TempC, string Symbol, double? HighC, double? LowC, DateTime Fetched)
{
    /// <summary>The next hours, two hours apart (a medium and a large tile show them).</summary>
    public IReadOnlyList<WeatherHour> Hours { get; init; } = [];

    /// <summary>The next days after today (a large tile shows them).</summary>
    public IReadOnlyList<WeatherDay> Days { get; init; } = [];
}

/// <summary>An hour ahead: its time, temperature and sky.</summary>
public sealed record WeatherHour(DateTime At, double TempC, string Symbol);

/// <summary>A day ahead: its date, lowest and highest temperature, and its sky around midday.</summary>
public sealed record WeatherDay(DateTime Date, double LowC, double HighC, string Symbol);

/// <summary>
/// The weather for the Weather tile (widgets part three, approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U; the user, 2026-11-10:
/// "yes Weather, MET Norway"). Only this tile uses the internet, and only while a widget that is on has one:
/// <list type="bullet">
/// <item>a place typed in the editor is looked up with OpenStreetMap's Nominatim (its name only);</item>
/// <item>the forecast comes from MET Norway (api.met.no) for that place's latitude and longitude, rounded to two
/// decimals (about a kilometre), at most every 30 minutes.</item>
/// </list>
/// Both services ask for an app name in the User-Agent; nothing about the person is sent.
/// </summary>
public static class WeatherFeed
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static WeatherFeed() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"RigSight/{ReleaseFeed.Current.ToString(3)} (+https://rigsight.app)");

    /// <summary>How long a forecast is kept before it's asked for again.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);

    /// <summary>The latest weather for each place asked for in this program, by <see cref="Key"/>.</summary>
    public static ConcurrentDictionary<string, WeatherNow> Latest { get; } = new();

    private static readonly ConcurrentDictionary<string, DateTime> Asked = new();

    /// <summary>A place's key: its latitude and longitude to two decimals.</summary>
    public static string Key(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(lat, 2):0.00},{Math.Round(lon, 2):0.00}");

    public static WeatherNow? For(WidgetTile t) => t is { Lat: double lat, Lon: double lon } && Latest.TryGetValue(Key(lat, lon), out var w) ? w : null;

    /// <summary>
    /// Asks for the weather of every weather tile given whose forecast is older than <see cref="Fresh"/>, in the
    /// background; a place that failed is asked again after ten minutes. Returns at once.
    /// </summary>
    public static void RefreshInBackground(IEnumerable<WidgetTile> tiles, Action? done = null)
    {
        var now = DateTime.UtcNow;
        foreach (var t in tiles)
        {
            if (t is not { Kind: WidgetTiles.Weather, Lat: double lat, Lon: double lon }) continue;
            string key = Key(lat, lon);
            if (Latest.TryGetValue(key, out var w) && now - w.Fetched.ToUniversalTime() < Fresh) continue;
            if (Asked.TryGetValue(key, out var at) && now - at < TimeSpan.FromMinutes(10)) continue;
            Asked[key] = now;
            _ = Task.Run(async () =>
            {
                var got = await GetAsync(lat, lon);
                if (got is not null)
                {
                    Latest[key] = got;
                    Asked.TryRemove(key, out _);
                    done?.Invoke();
                }
            });
        }
    }

    /// <summary>The weather now at a place, from MET Norway; null when it can't be had.</summary>
    public static async Task<WeatherNow?> GetAsync(double lat, double lon, CancellationToken ct = default)
    {
        try
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={Math.Round(lat, 2):0.00}&lon={Math.Round(lon, 2):0.00}");
            string json = await Http.GetStringAsync(url, ct);
            var w = Parse(json, DateTime.Now);
            Log.Write("weather", w is null ? $"No weather in the answer for {Key(lat, lon)}" : $"Weather for {Key(lat, lon)}: {w.TempC:0.#}°C, {w.Symbol}");
            return w;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write("weather", $"Weather for {Key(lat, lon)} couldn't be had: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// MET Norway's compact forecast: the first hour's temperature and symbol, the high and low of the rest of today, the
    /// next five hours two hours apart, and the next three days (their range, and their sky around midday).
    /// </summary>
    public static WeatherNow? Parse(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var props) || !props.TryGetProperty("timeseries", out var series) || series.GetArrayLength() == 0) return null;
        double? temp = null, high = null, low = null;
        string symbol = "";
        var hours = new List<WeatherHour>();
        var days = new Dictionary<DateTime, (double Low, double High, string Symbol, int Noon)>();
        static string SymbolOf(JsonElement data)
        {
            foreach (var next in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
                if (data.TryGetProperty(next, out var n) && n.TryGetProperty("summary", out var s) && s.TryGetProperty("symbol_code", out var code))
                    return code.GetString() ?? "";
            return "";
        }
        foreach (var step in series.EnumerateArray())
        {
            if (!step.TryGetProperty("time", out var timeEl) || !DateTime.TryParse(timeEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
            var local = utc.ToLocalTime();
            if (!step.TryGetProperty("data", out var data) || !data.TryGetProperty("instant", out var instant) || !instant.TryGetProperty("details", out var details)
                || !details.TryGetProperty("air_temperature", out var tEl)) continue;
            double t = tEl.GetDouble();
            string sym = SymbolOf(data);
            if (temp is null)
            {
                temp = t;
                symbol = sym;
            }
            if (local.Date == now.Date)
            {
                high = high is null ? t : Math.Max(high.Value, t);
                low = low is null ? t : Math.Min(low.Value, t);
            }
            if (local > now && local.Minute == 0 && local.Hour % 2 == 0 && hours.Count < 5 && sym.Length > 0) hours.Add(new WeatherHour(local, t, sym));
            if (local.Date > now.Date && local.Date <= now.Date.AddDays(3))
            {
                var d = days.TryGetValue(local.Date, out var was) ? was : (Low: t, High: t, Symbol: "", Noon: int.MaxValue);
                int fromNoon = Math.Abs(local.Hour - 12);
                days[local.Date] = (Math.Min(d.Low, t), Math.Max(d.High, t), sym.Length > 0 && fromNoon < d.Noon ? sym : d.Symbol, sym.Length > 0 && fromNoon < d.Noon ? fromNoon : d.Noon);
            }
        }
        if (temp is null) return null;
        return new WeatherNow(temp.Value, symbol, high, low, now)
        {
            Hours = hours,
            Days = [.. days.OrderBy(d => d.Key).Select(d => new WeatherDay(d.Key, d.Value.Low, d.Value.High, d.Value.Symbol))],
        };
    }

    /// <summary>Places that match a name typed in the editor, from OpenStreetMap's Nominatim (at most five).</summary>
    public static async Task<IReadOnlyList<WeatherPlace>> FindAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length < 2) return [];
        try
        {
            string url = $"https://nominatim.openstreetmap.org/search?format=jsonv2&limit=5&accept-language={Uri.EscapeDataString(Lang.Code)}&q={Uri.EscapeDataString(name)}";
            string json = await Http.GetStringAsync(url, ct);
            var found = ParsePlaces(json);
            Log.Write("weather", $"A place search found {found.Count}");
            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write("weather", $"A place search failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Nominatim's answer as places: the place's own name, and the rest of its address (its region and country) under it.</summary>
    public static IReadOnlyList<WeatherPlace> ParsePlaces(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<WeatherPlace>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (!p.TryGetProperty("lat", out var latEl) || !p.TryGetProperty("lon", out var lonEl)) continue;
            if (!double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
            string full = p.TryGetProperty("display_name", out var d) ? d.GetString() ?? "" : "";
            string own = p.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } nm ? nm : full.Split(',')[0].Trim();
            var parts = full.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0 && s != own && !s.Any(char.IsDigit)).ToList();
            string detail = parts.Count switch { 0 => "", 1 => parts[0], _ => $"{parts[^2]}, {parts[^1]}" };
            if (own.Length > 0 && list.All(x => x.Name != own || x.Detail != detail)) list.Add(new WeatherPlace(own, detail, lat, lon));
        }
        return list;
    }

    /// <summary>The kinds of sky a weather icon draws.</summary>
    public enum Sky { Clear, Partly, Cloudy, Fog, Rain, Snow, Thunder }

    /// <summary>MET Norway's symbol as the sky an icon draws, and whether it's night.</summary>
    public static (Sky Sky, bool Night) SkyOf(string symbol)
    {
        string s = Base(symbol);
        bool night = symbol.EndsWith("_night", StringComparison.Ordinal);
        var sky = s switch
        {
            "clearsky" => Sky.Clear,
            "fair" or "partlycloudy" => Sky.Partly,
            "fog" => Sky.Fog,
            _ when s.Contains("thunder") => Sky.Thunder,
            _ when s.Contains("snow") || s.Contains("sleet") => Sky.Snow,
            _ when s.Contains("rain") => Sky.Rain,
            _ => Sky.Cloudy,
        };
        return (sky, night);
    }

    /// <summary>The weather in a few words ("Partly cloudy", "Light rain"), for a tile with room.</summary>
    public static string Words(string symbol)
    {
        string s = Base(symbol);
        return s switch
        {
            "clearsky" => Lang.C("weather", "Clear"),
            "fair" => Lang.T("Mostly clear"),
            "partlycloudy" => Lang.T("Partly cloudy"),
            "cloudy" => Lang.T("Cloudy"),
            "fog" => Lang.T("Fog"),
            _ when s.Contains("thunder") => Lang.T("Thunder"),
            _ when s.Contains("sleet") => Lang.T("Sleet"),
            _ when s.StartsWith("light") && s.Contains("snow") => Lang.T("Light snow"),
            _ when s.Contains("snow") => Lang.T("Snow"),
            _ when s.StartsWith("light") && s.Contains("rain") => Lang.T("Light rain"),
            _ when s.StartsWith("heavy") && s.Contains("rain") => Lang.T("Heavy rain"),
            _ when s.Contains("rain") => Lang.T("Rain"),
            _ => "",
        };
    }

    private static string Base(string symbol) => symbol.Split('_')[0];
}
