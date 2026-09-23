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
        if (!s.Enabled) return null;

        var over = new List<string>();
        Track("CPU", k.CpuTemp, s.CpuLimit);
        Track("GPU", k.GpuTemp, s.GpuLimit);
        Track("GPU hot spot", k.GpuHotSpot, s.GpuHotSpotLimit);

        if (over.Count == 0 || nowMs - _lastAlertMs < s.CooldownMinutes * 60_000L) return null;
        _lastAlertMs = nowMs;
        string where = app is null ? "" : $" while {app} was running";
        return $"{string.Join(", ", over)} {(over.Count == 1 ? "has" : "have")} been above your limit for {s.SustainSeconds}+ seconds{where}.";

        void Track(string name, double? value, double limit)
        {
            if (value is double v && v > limit)
            {
                if (!_overSince.TryGetValue(name, out var since))
                    _overSince[name] = since = nowMs;
                if (nowMs - since >= s.SustainSeconds * 1000L)
                    over.Add($"{name} {Units.TempShort(v)}");
            }
            else
            {
                _overSince.Remove(name);
            }
        }
    }
}
