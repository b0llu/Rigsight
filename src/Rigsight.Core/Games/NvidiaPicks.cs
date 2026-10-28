using System.Text.Json;

namespace Rigsight.Core.Games;

/// <summary>NVIDIA's pick for a game on this PC: each setting by NVIDIA's name, at the step its slider recommends for a screen size.</summary>
public sealed record NvidiaPick(string Resolution, int Step, int Steps, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// NVIDIA's recommended settings for a game, read from the NVIDIA App's own files when it's on the PC (the person's ask,
/// 2026-10-27; RigSight never needs it). Under %LOCALAPPDATA%\NVIDIA Corporation\NVIDIA app\NvBackend: ApplicationStorage.json
/// lists the games it found (name, folder, programs); Recommendations\&lt;game&gt;\profiles_metadata.json names the current set
/// ("ce"), whose metadata.json gives the recommended step for each screen size under its kind of pick ("regular", or
/// "regular_rtx" for RTX cards), and that kind's folder holds pops.pub.tsv (JSON, whatever its name): the settings and their
/// value at each step. These files aren't documented, so anything missing or
/// shaped otherwise gives no pick. Only reads.
/// </summary>
public static class NvidiaPicks
{
    public static string ThisPc() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA Corporation", "NVIDIA app", "NvBackend");

    /// <summary>NVIDIA's pick for the game at <paramref name="resolution"/> ("2560x1440"), or at the main screen's size; null when there's none.</summary>
    public static NvidiaPick? Read(string backend, string? exePath, string? installDir, string? resolution, string? screen)
    {
        if (string.IsNullOrEmpty(backend) || !Directory.Exists(backend)) return null;
        try
        {
            if (ShortName(backend, exePath, installDir) is not { } game) return null;
            string root = Path.Combine(backend, "Recommendations", game);
            using var profiles = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "profiles_metadata.json")));
            if (profiles.RootElement.GetProperty("ce").GetString() is not { Length: > 0 } set) return null;
            // The set's metadata names its kind of pick, which is also its folder: "regular", or "regular_rtx" for RTX cards (REMATCH).
            using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, set, "metadata.json")));
            var kind = meta.RootElement.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("ops", out _));
            if (kind.Value.ValueKind != JsonValueKind.Object) return null;
            string regular = Path.Combine(root, set, kind.Name);
            string popsFile = kind.Value.TryGetProperty("pops", out var pf) && pf.TryGetProperty("filename", out var fn) && fn.GetString() is { Length: > 0 } name ? name : "pops.pub.tsv";
            var ops = kind.Value.GetProperty("ops").EnumerateArray()
                .Select(o => (Res: o.GetProperty("resolution").GetString() ?? "", Step: o.GetProperty("pops").GetInt32())).ToList();
            var op = ops.FirstOrDefault(o => o.Res == resolution);
            if (op.Res is null or "") op = ops.FirstOrDefault(o => o.Res == screen);
            if (op.Res is null or "" || op.Step < 1) return null;

            using var pops = JsonDocument.Parse(File.ReadAllText(Path.Combine(regular, Path.GetFileName(popsFile))));
            var names = pops.RootElement.GetProperty("settings").EnumerateArray().Select(s => s.EnumerateObject().First().Name).ToList();
            var steps = pops.RootElement.GetProperty("pops").EnumerateArray().ToList();
            if (op.Step > steps.Count) return null;
            var values = steps[op.Step - 1].GetProperty("values");
            var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
                if (values.TryGetProperty((i + 1).ToString(), out var v) && v.GetString() is { Length: > 0 } value) picked[names[i]] = value;
            return picked.Count == 0 ? null : new(op.Res, op.Step, steps.Count, picked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Log.Write("GameSettings", $"NVIDIA's pick couldn't be read ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>NVIDIA's short name for the game ("risk_of_rain_2"), found by its program or its folder.</summary>
    private static string? ShortName(string backend, string? exePath, string? installDir)
    {
        string file = Path.Combine(backend, "ApplicationStorage.json");
        if (!File.Exists(file)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        string dir = installDir?.TrimEnd('\\', '/') ?? "";
        foreach (var app in doc.RootElement.GetProperty("Applications").EnumerateArray())
        {
            if (!app.TryGetProperty("Application", out var a) || a.GetProperty("ShortName").GetString() is not { Length: > 0 } name) continue;
            bool byExe = exePath is { Length: > 0 } && a.TryGetProperty("DetectedFiles", out var files)
                && files.EnumerateArray().Any(f => string.Equals(f.GetString(), exePath, StringComparison.OrdinalIgnoreCase));
            bool byDir = dir.Length > 0 && a.TryGetProperty("InstallDirectory", out var d)
                && string.Equals(d.GetString()?.TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase);
            if (byExe || byDir) return name;
        }
        return null;
    }
}
