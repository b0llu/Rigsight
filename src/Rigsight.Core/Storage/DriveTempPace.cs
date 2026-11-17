namespace Rigsight.Core.Storage;

/// <summary>
/// Which drive readings count toward a day's average temperature: one per drive per 5 minutes. The drives are read
/// every 10 seconds while the app is open and every 5 minutes otherwise, so counting every reading would make the
/// average mostly the hours the window was open. The day's highest still takes every reading.
/// </summary>
public sealed class DriveTempPace
{
    public const long EveryMs = 300_000;

    // A reading up to 10 s early still counts: the closed-app reads come every 5 minutes, give or take a pass.
    private const long Slack = 10_000;

    private readonly Dictionary<string, (long Day, long At)> _counted = new(StringComparer.Ordinal);

    /// <summary>True when this drive's reading at <paramref name="nowMs"/> goes into the average for <paramref name="day"/>.</summary>
    public bool Counts(string key, long day, long nowMs)
    {
        if (_counted.TryGetValue(key, out var last) && last.Day == day && nowMs - last.At < EveryMs - Slack) return false;
        _counted[key] = (day, nowMs);
        return true;
    }
}
