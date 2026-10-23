using System.Text.RegularExpressions;
using Rigsight.Core.Games;

namespace Rigsight.Core.Ai;

/// <summary>What a button under an answer does.</summary>
public enum AiActionKind { CloseApp, CheckAgain, OpenPage, OpenWindows, VerifyGame, OpenSharing, StartAgent, CopyReport }

/// <summary>
/// A button under an answer, worked out by RigSight from the model's "[do: …]" line and checked against what is really
/// there: <see cref="Target"/> is the app's exe, the page, the Windows screen's address, or the game's verify link.
/// </summary>
/// <param name="Windowless">A Close's app has no window open (what the card says about it, measured, not the model's words).</param>
public sealed record AiAction(AiActionKind Kind, string Label, string Glyph, string Target = "", string? Focus = null, int Minutes = 0,
    CheckWhat What = CheckWhat.Fans, double MemMb = 0, bool Windowless = false);

/// <summary>What the buttons are checked against: this answer's look-ups, what was running when it looked, the games installed.</summary>
public sealed record AiActionContext(IReadOnlyList<AiLookUp> LookUps, RunningSnapshot? Running, Func<IReadOnlyList<InstalledGame>> Installed, bool AgentRunning);

/// <summary>
/// The buttons an answer may carry (the 2026-10-22 audit, rigsight-ai-actions-audit): the model asks for one with a
/// "[do: …]" line from a fixed list; RigSight decides what it does, from its own records, and drops any it can't stand
/// behind. Nothing in the model's words runs by itself: records can carry made-up names, so an app is only ever one this
/// answer saw running, a game one installed, a page or Windows screen one on the list.
/// </summary>
public static class AiActions
{
    public const int Most = 3;

    private static readonly Regex Line = new(@"^\[\s*do\s*:\s*(?<what>[^\]]+?)\s*\]$", RegexOptions.IgnoreCase);
    public static readonly Regex Inline = new(@"[ \t]*\[\s*do\s*:\s*(?<what>[^\]\n]+?)\s*\][ \t]*", RegexOptions.IgnoreCase);

    /// <summary>The words of a "[do: …]" line ("close python"), or null for any other line.</summary>
    public static string? Marker(string line) => Line.Match(line.Trim()) is { Success: true } m ? m.Groups["what"].Value.Trim() : null;

    /// <summary>The pages a button can open, with the name it shows.</summary>
    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crashes"] = Lang.Key("Crashes"), ["timeline"] = Lang.Key("Timeline"), ["games"] = Lang.Key("Games"), ["reports"] = Lang.Key("Reports"), ["temperatures"] = Lang.Key("Temperatures"),
        ["fans"] = Lang.Key("Fans"), ["memory"] = Lang.Key("Memory"), ["storage"] = Lang.Key("Storage"), ["network"] = Lang.Key("Network"), ["processes"] = Lang.Key("Processes"),
    };

    /// <summary>The Windows screens a button can open: only screens, never a change.</summary>
    private static readonly Dictionary<string, (string Uri, string Label, string Glyph)> Windows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["storage-sense"] = ("ms-settings:storagesense", Lang.Key("Open Storage Sense"), ""),
        ["disk-cleanup"] = ("cleanmgr.exe", Lang.Key("Open Disk Cleanup"), ""),
        ["startup-apps"] = ("ms-settings:startupapps", Lang.Key("Open Startup apps"), ""),
        ["update-history"] = ("ms-settings:windowsupdate-history", Lang.Key("Open Windows Update history"), ""),
        ["installed-apps"] = ("ms-settings:appsfeatures", Lang.Key("Open Installed apps"), ""),
        ["memory-diagnostic"] = ("mdsched.exe", Lang.Key("Open Windows Memory Diagnostic"), ""),
        ["network-status"] = ("ms-settings:network-status", Lang.Key("Open Network status"), ""),
    };

    /// <summary>The buttons for these "[do: …]" lines, at most <see cref="Most"/>, each checked; why one was dropped goes to the log.</summary>
    public static IReadOnlyList<AiAction> Resolve(IEnumerable<string> markers, AiActionContext context)
    {
        var actions = new List<AiAction>();
        foreach (var marker in markers)
        {
            if (actions.Count >= Most) { Log.Write("ask", $"[do: {marker}] dropped: already {Most} buttons"); continue; }
            var (action, why) = One(marker, context);
            if (action is null) { Log.Write("ask", $"[do: {marker}] dropped: {why}"); continue; }
            if (actions.Any(a => a.Kind == action.Kind && a.Target.Equals(action.Target, StringComparison.OrdinalIgnoreCase) && a.Focus == action.Focus)) continue;
            actions.Add(action);
        }
        return actions;
    }

    private static (AiAction? Action, string Why) One(string marker, AiActionContext c)
    {
        var words = marker.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "", rest = words.Length > 1 ? words[1].Trim().Trim('"', '\'') : "";
        switch (verb)
        {
            case "close":
            {
                if (c.Running is null || !c.LookUps.Any(l => l.Tool == "get_running_now")) return (null, "this answer didn't look at what is running now");
                if (c.Running.Find(rest) is not { } app) return (null, $"'{rest}' wasn't running when it looked");
                if (!app.CanClose) return (null, $"{app.Exe} isn't one an answer may close ({app.Risk}, {app.Category}{(app.InFront ? ", in front" : "")})");
                return (new AiAction(AiActionKind.CloseApp, Lang.T($"Close {app.Name}"), "", app.Exe, app.Name, MemMb: app.MemMb, Windowless: !app.HasWindow), "");
            }
            case "check":
            {
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int minutes = parts.Length > 0 && int.TryParse(parts[0], out int m) ? m : 10;
                minutes = Math.Clamp((int)Math.Round(minutes / 5.0) * 5, 5, 60);
                if (ActionCheck.Parse(parts.Length > 1 ? parts[1] : "fans") is not { } what) return (null, $"nothing to check called '{(parts.Length > 1 ? parts[1] : "")}'");
                return (new AiAction(AiActionKind.CheckAgain, Lang.T($"Check again in {minutes} min"), "", what.ToString(), Minutes: minutes, What: what), "");
            }
            case "open":
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string page = parts.Length > 0 ? parts[0].ToLowerInvariant() : "", focus = parts.Length > 1 ? parts[1] : "";
                if (!Pages.TryGetValue(page, out var shown)) return (null, $"no page called '{page}'");
                string name = Lang.T(shown);
                if (page == "storage" && Regex.Match(focus, @"^([A-Za-z])(:|\b)") is { Success: true } drive)
                    return (new AiAction(AiActionKind.OpenPage, Lang.T($"Open Storage on the {drive.Groups[1].Value.ToUpperInvariant()} drive"), "", page, drive.Groups[1].Value.ToUpperInvariant()), "");
                if (page == "games" && focus.Length > 0)
                    return Installed(c, focus) is { } g
                        ? (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {g.Name}"), "", page, g.Key), "")
                        : (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {name}"), "", page), "");
                if (page == "processes" && focus.Length > 0)
                    return (new AiAction(AiActionKind.OpenPage, Lang.T("Open Processes by memory"), "", page, focus.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : "memory"), "");
                return (new AiAction(AiActionKind.OpenPage, Lang.T($"Open {name}"), "", page), "");
            }
            case "windows":
                return Windows.TryGetValue(rest.Replace(' ', '-'), out var screen)
                    ? (new AiAction(AiActionKind.OpenWindows, Lang.T(screen.Label), screen.Glyph, screen.Uri), "")
                    : (null, $"no Windows screen called '{rest}'");
            case "verify":
            {
                if (Installed(c, rest) is not { } game) return (null, $"'{rest}' isn't an installed game");
                if (game.Launcher != Launcher.Steam || !game.Key.StartsWith("steam:", StringComparison.Ordinal)) return (null, $"{game.Name} isn't a Steam game (only Steam has a check-the-files link)");
                return (new AiAction(AiActionKind.VerifyGame, Lang.T($"Verify {game.Name}'s files"), "", "steam://validate/" + game.Key["steam:".Length..], game.Name), "");
            }
            case "settings" or "sharing" or "turn-on":
                return (new AiAction(AiActionKind.OpenSharing, Lang.T("Open What RigSight sends"), ""), "");
            case "start-agent" or "agent" when !c.AgentRunning:
                return (new AiAction(AiActionKind.StartAgent, Lang.T("Start the agent"), ""), "");
            case "start-agent" or "agent":
                return (null, "the agent is already running");
            case "copy-report" or "report":
                return (new AiAction(AiActionKind.CopyReport, Lang.T("Copy problem report"), ""), "");
            default:
                return (null, $"no action called '{verb}'");
        }
    }

    private static InstalledGame? Installed(AiActionContext c, string named)
    {
        if (named.Length == 0) return null;
        var games = c.Installed();
        return games.FirstOrDefault(g => g.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
            ?? games.FirstOrDefault(g => g.Name.Replace("®", "").Replace("™", "").Contains(named, StringComparison.OrdinalIgnoreCase));
    }
}
