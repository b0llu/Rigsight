using Microsoft.Win32;

namespace Rigsight.Services;

/// <summary>A one-time summary of this PC (for crash reports people paste into forums), read from the registry.</summary>
public static class PcInfo
{
    private static readonly Lazy<string> Summary = new(Build);

    /// <summary>"CPU: … / GPU: … (driver …) / RAM: 16 GB / Windows 11 24H2 (build 26200)".</summary>
    public static string Text => Summary.Value;

    private static string Build()
    {
        var lines = new List<string>();
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (cpu?.GetValue("ProcessorNameString") is string name) lines.Add($"CPU: {name.Trim()}");
        }
        catch { }
        try
        {
            // Display adapters: one numbered subkey each; skip Windows' fallback adapter and remote-desktop ones.
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                using var key = cls!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc || desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Remote", StringComparison.OrdinalIgnoreCase) || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("Mirror", StringComparison.OrdinalIgnoreCase) || desc.Contains("Indirect", StringComparison.OrdinalIgnoreCase)) continue;
                string version = key.GetValue("DriverVersion") as string ?? "?";
                // NVIDIA's Windows version "32.0.16.1692" is the driver people know as 616.92.
                if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && version.Split('.') is [_, _, var third, var fourth] && third.Length >= 1 && fourth.Length == 4)
                {
                    var digits = third[^1] + fourth;
                    version = $"{digits[..3]}.{digits[3..]} ({version})";
                }
                string date = key.GetValue("DriverDate") as string ?? "";
                lines.Add($"GPU: {desc} (driver {version}{(date.Length > 0 ? $", {date}" : "")})");
            }
        }
        catch { }
        try
        {
            double gb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;
            lines.Add($"RAM: {Math.Round(gb):0} GB");
        }
        catch { }
        try
        {
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string build = nt?.GetValue("CurrentBuild") as string ?? "";
            string display = nt?.GetValue("DisplayVersion") as string ?? "";
            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            string product = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            lines.Add($"OS: {product} {display} (build {build})".Replace("  ", " "));
        }
        catch { }
        return string.Join(Environment.NewLine, lines);
    }
}
