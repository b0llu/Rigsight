using Rigsight.Agent.Sensors;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent;

/// <summary>Raises an alert when a temperature stays above its limit for a while (brief spikes are ignored).</summary>
internal sealed class AlertMonitor
{
    private readonly Dictionary<string, long> _overSince = [];
    private long _lastAlertMs = long.MinValue / 2;

    /// <summary>The readings the last alert was about ("CPU", "GPU", the hot spot), for its "usually sits at" sentence.</summary>
    public IReadOnlyList<string> LastParts { get; private set; } = [];

    /// <summary>Returns the alert's title and words when one should be shown now, else null.</summary>
    public (string Title, string Body)? Check(KeyValues k, string? app, AlertSettings s, long nowMs)
    {
        if (!s.Enabled)
        {
            // Nothing is watched while off: turned back on, a limit has to be passed for the whole time again.
            _overSince.Clear();
            return null;
        }

        var over = new List<NoticeWords.Hot>();
        Track("CPU", k.CpuTemp, s.CpuLimit);
        Track("GPU", k.GpuTemp, s.GpuLimit);
        Track(Lang.Key("GPU hot spot"), k.GpuHotSpot, s.GpuHotSpotLimit);

        if (over.Count == 0 || nowMs - _lastAlertMs < s.CooldownMinutes * 60_000L) return null;
        _lastAlertMs = nowMs;
        LastParts = [.. over.Select(h => h.Part).Distinct()];
        return NoticeWords.Running(over, s.SustainSeconds, app);

        void Track(string name, double? value, double limit)
        {
            if (value is double v && v > limit)
            {
                if (!_overSince.TryGetValue(name, out var since))
                    _overSince[name] = since = nowMs;
                if (nowMs - since >= s.SustainSeconds * 1000L)
                    over.Add(new NoticeWords.Hot(name, v, limit));
            }
            else
            {
                _overSince.Remove(name);
            }
        }
    }

}
