using System.Text.Json;
using System.Text.Json.Serialization;
using Rigsight.Core.Rewind;

namespace Rigsight.Rewind;

/// <summary>
/// Test copies only: the month's real rewind turned into the cases this PC doesn't have, so they can be watched in the
/// app (a PC that never plays games, one that plays a little, and one where nothing went wrong). Never made outside a
/// test copy; each says it is a sample in the list and the viewer.
/// </summary>
public static class RewindSamples
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static RewindFacts Copy(RewindFacts f, string suffix, string name)
    {
        var c = JsonSerializer.Deserialize<RewindFacts>(JsonSerializer.Serialize(f, Json), Json)!;
        c.Id = $"{f.Id}~{suffix}";
        c.Sample = name;
        return c;
    }

    public static IEnumerable<RewindFacts> Of(RewindFacts real)
    {
        // Never plays games: no game time at all.
        var none = Copy(real, "no-games", "No games");
        none.GamingSec = 0; none.GameCount = 0; none.Plays = 0; none.TopGames = []; none.LongestGame = null; none.LongestStart = null;
        none.LongestEnd = null; none.PlayHours = new double[24]; none.NightShare = 0;
        yield return none;

        // Plays a little: two games, a few hours.
        var two = Copy(real, "two-games", "Two games");
        two.TopGames = [.. real.TopGames.Take(2).Select((g, i) => g with { Value = i == 0 ? 2.6 * 3600 : 1.1 * 3600, Count = i == 0 ? 3 : 2 })];
        two.GameCount = two.TopGames.Count; two.Plays = 5; two.GamingSec = two.TopGames.Sum(g => g.Value);
        if (two.TopGames.Count > 0) two.LongestGame = two.TopGames[0] with { Value = 4900 };
        double scale = real.GamingSec > 0 ? two.GamingSec / real.GamingSec : 0;
        two.PlayHours = [.. real.PlayHours.Select(h => h * scale)];
        yield return two;

        // Nothing went wrong: no crash, no shut-off, nothing changed, and it ran cool.
        var quiet = Copy(real, "quiet", "Quiet PC");
        quiet.AppCrashes = 0; quiet.ShutOffs = 0;
        quiet.CrashesByDay = [.. real.CrashesByDay.Select(_ => 0)]; quiet.ShutOffsByDay = [.. real.ShutOffsByDay.Select(_ => 0)];
        quiet.NewDriver = null; quiet.NewDriverAt = null; quiet.Installed = 0; quiet.Updated = 0; quiet.Removed = 0;
        if (quiet.HotSpot is { } hs) quiet.HotSpot = hs with { Value = 66 };
        if (quiet.GpuHot is { } g) quiet.GpuHot = g with { Value = 58 };
        if (quiet.CpuHot is { } c) quiet.CpuHot = c with { Value = 61 };
        yield return quiet;
    }
}
