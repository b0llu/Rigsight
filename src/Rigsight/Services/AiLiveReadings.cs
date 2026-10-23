using System.Globalization;
using System.Text;
using System.Windows;
using Rigsight.Core;
using Rigsight.ViewModels;

namespace Rigsight.Services;

/// <summary>
/// The live readings the app holds, as text for the AI helper's get_sensors_now and get_today (AiTools.SensorsNow,
/// AiTools.DrivesNow): every sensor All sensors shows, by part, and each drive's health. Sensor names are the
/// hardware's own (never a name the person typed for one), and nothing names a file, folder or user.
/// </summary>
public static class AiLiveReadings
{
    /// <summary>Hooks for AiTools: read on the app's own thread, since the look-ups run on another one.</summary>
    public static Func<string> Sensors(LiveData live) => () => OnUi(() => SensorsText(live));
    public static Func<string> Drives(LiveData live) => () => OnUi(() => DrivesText(live));

    private static string OnUi(Func<string> read)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) return read();
        try { return app.Dispatcher.Invoke(read, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(5)) ?? ""; }
        catch (TimeoutException) { return ""; }
    }

    public static string SensorsText(LiveData live)
    {
        if (!live.HasHardware || live.Hardware.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"Every reading right now ({DateTime.Now.ToString("h:mm:ss tt", CultureInfo.InvariantCulture)}), by part:");
        foreach (var part in live.Hardware)
        {
            var readings = part.Sensors.Where(s => s.Value is not null)
                .Select(s => $"{SensorWords.Show(s.Name)} {Units.Format(s.Kind, s.Value)}").ToList();
            if (readings.Count == 0) continue;
            sb.AppendLine($"  {part.ShownName}: {string.Join(", ", readings)}");
        }
        return sb.ToString();
    }

    public static string DrivesText(LiveData live)
    {
        if (live.Drives.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var d in live.Drives)
        {
            var parts = new List<string>();
            if (d.Health?.Status is { } status && status != "Unknown") parts.Add($"health {status}");
            if (d.Life?.Value is double life) parts.Add($"{life:0}% of its rated life left");
            if (d.Health?.ReallocatedSectors is > 0 || d.Health?.PendingSectors is > 0 || d.Health?.UncorrectableSectors is > 0)
                parts.Add($"sectors moved {d.Health.ReallocatedSectors ?? 0}, waiting {d.Health.PendingSectors ?? 0}, unreadable {d.Health.UncorrectableSectors ?? 0}");
            if (d.Temperature?.Value is double t) parts.Add($"{t:0} °C");
            if (d.UsedSpace?.Value is double used) parts.Add($"{used:0}% full");
            if (parts.Count > 0) sb.AppendLine($"  {d.Name}: {string.Join(", ", parts)}");
        }
        return sb.ToString();
    }
}
