using Rigsight.Agent.Sensors;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent;

/// <summary>Raises an alert when a temperature stays above its limit for a while (brief spikes are ignored).</summary>
internal sealed class AlertMonitor
{
    private readonly Dictionary<string, long> _overSince = [];
    private long _lastAlertMs = long.MinValue / 2;

    /// <summary>Returns alert text when one should be shown now, else null.</summary>
    public string? Check(KeyValues k, string? app, AlertSettings s, long nowMs)
    {
        if (!s.Enabled)
        {
            // Nothing is watched while off: turned back on, a limit has to be passed for the whole time again.
            _overSince.Clear();
            return null;
        }

        var over = new List<string>();
        Track("CPU", k.CpuTemp, s.CpuLimit);
        Track("GPU", k.GpuTemp, s.GpuLimit);
        Track(Lang.Key("GPU hot spot"), k.GpuHotSpot, s.GpuHotSpotLimit);

        if (over.Count == 0 || nowMs - _lastAlertMs < s.CooldownMinutes * 60_000L) return null;
        _lastAlertMs = nowMs;
        return Message(over, s.SustainSeconds, app);

        void Track(string name, double? value, double limit)
        {
            if (value is double v && v > limit)
            {
                if (!_overSince.TryGetValue(name, out var since))
                    _overSince[name] = since = nowMs;
                if (nowMs - since >= s.SustainSeconds * 1000L)
                    over.Add($"{Lang.T(name)} {Units.TempShort(v)}");
            }
            else
            {
                _overSince.Remove(name);
            }
        }
    }

    /// <summary>
    /// The alert's text: "GPU 86° has been above your limit for 30+ seconds while Cyberpunk 2077 was running.";
    /// <paramref name="over"/> holds each reading over its limit with its value ("GPU 86°").
    /// </summary>
    public static string Message(IReadOnlyList<string> over, int sustainSeconds, string? app)
    {
        string readings = string.Join(", ", over);
        return (over.Count == 1, app is null) switch
        {
            (true, true) => Lang.T($"{readings} has been above your limit for {sustainSeconds}+ seconds."),
            (true, false) => Lang.T($"{readings} has been above your limit for {sustainSeconds}+ seconds while {app} was running."),
            (false, true) => Lang.T($"{readings} have been above your limit for {sustainSeconds}+ seconds."),
            _ => Lang.T($"{readings} have been above your limit for {sustainSeconds}+ seconds while {app} was running."),
        };
    }
}
