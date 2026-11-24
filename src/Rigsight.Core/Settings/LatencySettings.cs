namespace Rigsight.Core.Settings;

/// <summary>
/// The Latency page's own setting (issue #15, approved mock-up 2026-11-24): whether the app wake-up check runs. On by
/// default, turned off with the switch on the page; nothing else turns it back on, an update included (see
/// rigsight-settings-survive-updates). Turning it off stops only that check: driver delays are still recorded.
/// </summary>
public sealed class LatencySettings
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>The app wake-up check runs (8 checks a second, 64 while a game is in front, sound plays or the page is open, 2 on battery, none with Battery Saver on).</summary>
    public bool WakeCheck { get; set; } = true;
}
