using System.Globalization;

namespace Rigsight.Core.Ai;

/// <summary>A model RigSight suggests for Ollama: its tag, name, download size and why it's a good pick.</summary>
public sealed record OllamaPick(string Tag, string Name, double Gb, string Why)
{
    public string SizeText => Lang.T($"{Gb.ToString("0.#", CultureInfo.CurrentCulture)} GB download");
}

/// <summary>How a model fits the graphics card: all of it, part of it (the rest runs on the processor, slower), or not well at all.</summary>
public enum OllamaFit { Unknown, Fits, PartFits, TooBig }

/// <summary>
/// The models picked for this PC (Settings › Ask RigSight → Ollama, approved mock-up 2026-10-24): one best for its
/// graphics card's memory, one smaller and one bigger. Every pick can look things up (Ollama's "tools"), checked on
/// ollama.com in October 2026; sizes are Ollama's download sizes.
/// </summary>
public static class OllamaPicks
{
    /// <summary>Smallest first; <see cref="BestFor"/> walks them by the card's memory.</summary>
    public static readonly IReadOnlyList<OllamaPick> All =
    [
        new("qwen3.5:4b", "Qwen 3.5, 4B", 3.5, Lang.Key("Small enough for a PC without a strong graphics card. Answers come slower.")),
        new("qwen3.5:9b", "Qwen 3.5, 9B", 6.6, Lang.Key("Quick, and still good at looking things up.")),
        new("gemma4:12b", "Gemma 4, 12B", 8, Lang.Key("Good answers, and leaves room on your card for a game.")),
        new("gpt-oss:20b", "gpt-oss, 20B", 14, Lang.Key("Better answers, for a card with lots of memory.")),
        new("gemma4:26b", "Gemma 4, 26B", 18, Lang.Key("The best answers here, for the biggest cards.")),
    ];

    /// <summary>The pick for a card with <paramref name="vramGb"/> GB (none or unknown: the smallest).</summary>
    public static int BestFor(double? vramGb) => vramGb switch
    {
        >= 20 => 4,
        >= 14 => 3,
        >= 10 => 2,
        >= 6 => 1,
        _ => 0,
    };

    /// <summary>The best pick, then one smaller and one bigger when there is one.</summary>
    public static (OllamaPick Best, OllamaPick? Smaller, OllamaPick? Bigger) For(double? vramGb)
    {
        int i = BestFor(vramGb);
        return (All[i], i > 0 ? All[i - 1] : null, i < All.Count - 1 ? All[i + 1] : null);
    }

    /// <summary>
    /// All of it on the card, part of it (Ollama runs the rest on the processor, which is slower), or so much off the
    /// card that answers would crawl. Unknown without the card's memory (no reading yet, or Ollama on another computer).
    /// </summary>
    public static OllamaFit Fit(double gb, double? vramGb) => vramGb switch
    {
        null or <= 0 => OllamaFit.Unknown,
        { } v when gb <= v => OllamaFit.Fits,
        { } v when gb <= v * 1.5 => OllamaFit.PartFits,
        _ => OllamaFit.TooBig,
    };

    /// <summary>"Fits your RTX 3080 Ti", "May be slow", "Too big for this PC", or nothing when it can't be told.</summary>
    public static string FitText(OllamaFit fit, string? card) => fit switch
    {
        OllamaFit.Fits => card is { Length: > 0 } ? Lang.T($"Fits your {card}") : Lang.T("Fits"),
        OllamaFit.PartFits => Lang.T("May be slow"),
        OllamaFit.TooBig => Lang.T("Too big for this PC"),
        _ => "",
    };

    /// <summary>The hover on "May be slow" and "Too big": what happens when it doesn't fit.</summary>
    public static string FitWhy(OllamaFit fit) => fit switch
    {
        OllamaFit.PartFits => Lang.T("Only part of it fits on your graphics card, so answers take longer."),
        OllamaFit.TooBig => Lang.T("Most of it won't fit on your graphics card, so answers would take minutes."),
        _ => "",
    };

    /// <summary>"RTX 3080 Ti" from "NVIDIA GeForce RTX 3080 Ti": the maker's words dropped, as people say it.</summary>
    public static string ShortCard(string name)
    {
        var n = name.Trim();
        foreach (var maker in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel " })
            if (n.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) { n = n[maker.Length..]; break; }
        return n.Length > 0 ? n : name.Trim();
    }

    /// <summary>
    /// The graphics card models would run on, from the parts list: a separate card over a built-in one, as Ollama does.
    /// </summary>
    public static string? MainCard(IEnumerable<string> cards)
    {
        var list = cards.Where(c => !c.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
            && !c.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)).ToList();
        return list.FirstOrDefault(c => c.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || c.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)
                || c.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    /// <summary>
    /// Where Ollama keeps its models: its OLLAMA_MODELS setting, or .ollama\models in the user folder. Downloads go
    /// there, as with "ollama pull", never into RigSight's folder.
    /// </summary>
    public static string ModelsFolder()
    {
        string? set = Environment.GetEnvironmentVariable("OLLAMA_MODELS")
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.Machine);
        return set is { Length: > 0 } ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");
    }

    /// <summary>True when Ollama is installed on this PC (its own installer's folder, or ollama.exe on the PATH), running or not.</summary>
    public static bool Installed() => OllamaExe() is not null;

    private static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama");

    private static string? OllamaExe()
    {
        var local = Path.Combine(InstallFolder, "ollama.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (Path.Combine(dir.Trim('"'), "ollama.exe") is var exe && File.Exists(exe)) return exe; }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Starts Ollama as opening it from the Start menu does: its own app (the tray icon, which runs the server), or else
    /// "ollama serve" with no window. False when it isn't installed or Windows wouldn't start it.
    /// </summary>
    public static bool Start()
    {
        try
        {
            var app = Path.Combine(InstallFolder, "ollama app.exe");
            if (File.Exists(app))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = InstallFolder })?.Dispose();
                Log.Write("ollama", $"Started Ollama ({app})");
                return true;
            }
            if (OllamaExe() is { } exe)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
                Log.Write("ollama", $"Started Ollama ({exe} serve)");
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write("ollama", $"Couldn't start Ollama: {ex.Message}"); }
        return false;
    }

    /// <summary>The free space where Ollama keeps models, for "It goes to C: (412 GB free)"; null when it can't be read.</summary>
    public static (string Drive, long Free)? FreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(ModelsFolder()));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return (root.TrimEnd('\\'), drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
