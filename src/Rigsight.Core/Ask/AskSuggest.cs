using System.Text.RegularExpressions;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ask;

/// <summary>
/// Questions to offer while one is being typed: ones that have an answer, worded with the app and the time already
/// typed ("rematch hot" offers "How hot does REMATCH make my PC?"). Matched by the words typed, so it costs nothing
/// and needs no model: a key press never loads one.
/// </summary>
public static partial class AskSuggest
{
    /// <summary>A question that can be asked: {when} takes the time typed (or the one after the bar), {app} an app, {game} a game.</summary>
    public sealed record Template(AskIntent Intent, string Text, string Tags = "");

    public static readonly Template[] Templates =
    [
        new(AskIntent.Crashes, "Why did my PC crash {when|yesterday}?", "crashed bsod blue screen restart reboot froze shutdown cause reason"),
        new(AskIntent.Crashes, "Did my PC crash {when|this week}?", "crashes crashed any bsod problems"),
        new(AskIntent.Crashes, "Why did {app} crash {when|}?", "crashed crashing cause reason froze closed"),
        new(AskIntent.Crashes, "Has {app} crashed {when|this month}?", "crash crashes crashing"),
        new(AskIntent.Crashes, "Were there any blue screens {when|this month}?", "bsod bluescreen crash"),
        new(AskIntent.Crashes, "Why did my PC shut down by itself {when|yesterday}?", "turn off turned restart restarted reboot power sudden"),
        new(AskIntent.Changes, "What changed on my PC {when|this week}?", "changes new different update updated"),
        new(AskIntent.Changes, "What was installed {when|this month}?", "install new apps programs added"),
        new(AskIntent.Changes, "What was uninstalled {when|this month}?", "removed deleted gone uninstall"),
        new(AskIntent.Changes, "When did my graphics driver last change?", "nvidia amd gpu update updated installed version"),
        new(AskIntent.Changes, "When did Windows last update?", "updates updated installed patch"),
        new(AskIntent.Temps, "How hot did my PC get {when|today}?", "temperature temps temp heat warm max highest"),
        new(AskIntent.Temps, "How hot did my GPU get {when|yesterday}?", "graphics card temperature temps temp heat max highest"),
        new(AskIntent.Temps, "How hot did my CPU get {when|yesterday}?", "processor temperature temps temp heat max highest"),
        new(AskIntent.Temps, "Is my PC overheating?", "temperature temps too hot heat ok safe"),
        new(AskIntent.Temps, "How hot does {game} make my PC?", "temperature temps temp heat warm"),
        new(AskIntent.Usage, "How long was I on my PC {when|today}?", "screen time hours used use usage spent"),
        new(AskIntent.Usage, "How much did I play {when|this week}?", "gaming games played hours time long"),
        new(AskIntent.Usage, "How long did I play {game} {when|this week}?", "played hours time much"),
        new(AskIntent.Usage, "When did I last play {game}?", "played last time recently"),
        new(AskIntent.Usage, "How long did I use {app} {when|this week}?", "used hours time much spent"),
        new(AskIntent.Usage, "When did I last use {app}?", "used opened last time recently"),
        new(AskIntent.Usage, "What time did I start {when|today}?", "turn on began first when"),
        new(AskIntent.TopApps, "What did I use most {when|this week}?", "apps most used top time spent"),
        new(AskIntent.TopApps, "Which games did I play most {when|this month}?", "top most played gaming"),
        new(AskIntent.Slow, "Why was my PC slow {when|today}?", "laggy lag lagging stutter sluggish performance fps"),
        new(AskIntent.Slow, "What was heavy on my PC {when|yesterday}?", "hogging cpu busy background load using"),
        new(AskIntent.Slow, "Why was {game} laggy {when|yesterday}?", "slow lag stutter fps performance choppy"),
        new(AskIntent.Memory, "What used the most memory {when|today}?", "ram using eating usage apps"),
        new(AskIntent.Memory, "Do I need more RAM?", "memory enough upgrade full"),
        new(AskIntent.Memory, "How much memory does {app} use?", "ram usage using"),
        new(AskIntent.Network, "How much data did I use {when|this month}?", "internet download downloaded upload usage gb network"),
        new(AskIntent.Network, "What used the most data {when|this week}?", "internet download bandwidth apps network using"),
        new(AskIntent.Network, "Did my internet drop {when|today}?", "wifi disconnect disconnected cut out connection network down"),
        new(AskIntent.Network, "How fast is my internet?", "speed mbps download slow network wifi"),
        new(AskIntent.Network, "How much data did {app} use {when|this month}?", "internet download downloaded upload network"),
        new(AskIntent.Storage, "How full are my drives?", "disk space storage free left ssd"),
        new(AskIntent.Storage, "How much space is left on C:?", "disk drive storage free full ssd"),
        new(AskIntent.Storage, "Why is my drive filling up?", "disk space storage full taking"),
        new(AskIntent.Fans, "Are my fans working?", "fan speed rpm broken stopped"),
        new(AskIntent.Fans, "How fast are my fans spinning?", "fan speed rpm loud noise"),
        new(AskIntent.Health, "How is my PC doing {when|today}?", "ok okay health healthy summary status alright"),
        new(AskIntent.Health, "How was my PC {when|yesterday}?", "summary recap what happened"),
        new(AskIntent.Health, "Anything wrong with my PC {when|this week}?", "problems issues worry health ok"),
        new(AskIntent.Specs, "What's in this PC?", "specs specifications parts hardware what do i have"),
        new(AskIntent.Specs, "What graphics card do I have?", "gpu video nvidia amd specs"),
        new(AskIntent.Specs, "Which graphics driver is installed?", "nvidia amd gpu version"),
        new(AskIntent.Specs, "How much RAM do I have?", "memory installed specs gb"),
        new(AskIntent.Specs, "What is my BIOS version?", "motherboard firmware"),
        new(AskIntent.Specs, "What Windows version is this?", "os build"),
        new(AskIntent.Metric, "What was my average CPU load {when|today}?", "usage processor busy utilization"),
        new(AskIntent.Metric, "What was my average GPU load {when|today}?", "usage graphics card busy utilization"),
        new(AskIntent.Metric, "How much power did my GPU draw {when|yesterday}?", "watts wattage graphics card consumption"),
        new(AskIntent.Metric, "What was the CPU load while I used {app}?", "usage processor busy average"),
        new(AskIntent.Usage, "Did I play more this week than last week?", "compare gaming games less versus"),
        new(AskIntent.Usage, "Was I on my PC more this week than last week?", "compare screen time less versus hours"),
        new(AskIntent.Temps, "Was my GPU hotter this week than last week?", "compare temperature temps warmer cooler versus"),
        new(AskIntent.Network, "Did I use more data this month than last month?", "compare internet download less versus"),
        new(AskIntent.Crashes, "Were there more crashes this month than last month?", "compare problems fewer versus"),
        new(AskIntent.Help, "What can you do?", "help ask questions examples"),
    ];

    /// <summary>Words too common to tell one question from another.</summary>
    private static readonly HashSet<string> Small = new(StringComparer.Ordinal)
    {
        "my", "the", "a", "an", "is", "are", "was", "were", "did", "do", "does", "i", "me", "on", "in", "of", "to", "it", "this", "that", "pc", "computer", "any", "there",
    };

    /// <summary>Up to <paramref name="count"/> questions that fit what's typed so far, best first; none for under two letters.</summary>
    public static List<string> For(string typed, IReadOnlyList<AskApp> apps, DateTime now, int count = 4)
    {
        typed = typed.Trim();
        if (typed.Length < 2) return [];
        var (period, rest) = AskTime.Parse(typed, now);
        var (named, without, _) = AskRouter.FindApp(rest, apps);
        string lower = typed.ToLowerInvariant();
        var words = Word().Matches(without.ToLowerInvariant()).Select(m => m.Value).ToList();
        var telling = words.Where(w => !Small.Contains(w)).ToList();
        // Nothing but a time or an app typed yet: every question about it fits.
        bool bare = telling.Count == 0;
        if (bare && period is null && named is null) telling = words;

        var topGame = apps.Where(a => a.Category == AppCategory.Game).MaxBy(a => a.ActiveSec);
        var topApp = apps.Where(a => a.Category is not (AppCategory.Game or AppCategory.System)).MaxBy(a => a.ActiveSec);

        var scored = new List<(double Score, int Order, string Text)>();
        for (int i = 0; i < Templates.Length; i++)
        {
            var t = Templates[i];
            bool wantsGame = t.Text.Contains("{game}"), wantsApp = t.Text.Contains("{app}");
            AskApp? app = null;
            if (wantsGame || wantsApp)
            {
                app = named is not null && (!wantsGame || named.Category == AppCategory.Game) ? named : named is null ? (wantsGame ? topGame : topApp) : null;
                if (app is null) continue;
            }
            else if (named is not null) continue; // an app was typed: only questions about an app

            string text = Fill(t.Text, period, app);
            var have = Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
            var tags = t.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            double score = 0;
            int missed = 0;
            for (int w = 0; w < telling.Count; w++)
            {
                string word = telling[w];
                bool last = w == telling.Count - 1 && !lower.EndsWith(' '); // the word still being typed
                bool Fits(string candidate) => candidate == word || ((last || word.Length >= 4) && candidate.StartsWith(word, StringComparison.Ordinal));
                if (have.Any(Fits)) score += 1;
                else if (tags.Any(Fits)) score += 0.8;
                else missed++;
            }
            if (!bare && (missed > (telling.Count >= 3 ? 1 : 0) || score == 0)) continue;
            if (text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) score += 3; // typing this very question
            if (named is not null && app == named) score += 0.5;
            scored.Add((score - missed, i, text));
        }
        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Select(s => s.Text)
            .Where(s => !string.Equals(s, typed, StringComparison.OrdinalIgnoreCase)).Distinct().Take(count)];
    }

    /// <summary>A template as a question: the time typed (or its own), the app by its name.</summary>
    public static string Fill(string template, AskPeriod? period, AskApp? app)
    {
        string text = WhenSlot().Replace(template, m => period?.Label ?? m.Groups[1].Value);
        if (app is not null) text = text.Replace("{game}", app.Name).Replace("{app}", app.Name);
        return Spaces().Replace(text, " ").Replace(" ?", "?");
    }

    [GeneratedRegex(@"\{when\|([^}]*)\}")]
    private static partial Regex WhenSlot();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
