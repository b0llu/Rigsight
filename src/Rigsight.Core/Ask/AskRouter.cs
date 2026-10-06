using System.Text.RegularExpressions;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Ask;

/// <summary>A way of asking this user taught it: the wording (without its time, its app as "this app") and what it means.</summary>
public sealed record AskLearned(string Text, AskIntent Intent, bool Why);

/// <summary>An app a question can name: what it's called, and how much it's used (the likelier one when two match).</summary>
public sealed record AskApp(long Id, string Name, string Exe, AppCategory Category, double ActiveSec);

/// <summary>
/// Reads a question: what it asks for (the nearest known way of asking, by meaning), and the time, app and part of the
/// PC it names (found by rule, never guessed). A short follow-up ("and the day before?") leans on the question before.
/// </summary>
public sealed partial class AskRouter
{
    /// <summary>At or over this, the reading is taken; under it, the question is asked back.</summary>
    public const double Sure = 0.40;

    /// <summary>What a telling word adds to its intent ("internet" in a question about what's "hogging" something).</summary>
    private const double Boost = 0.08;

    private readonly IAskEmbedder? _embedder;
    private float[][]? _examples;
    private readonly Lock _gate = new();

    /// <param name="embedder">Null: no language model (it failed to load); questions are then read by their words alone.</param>
    public AskRouter(IAskEmbedder? embedder) => _embedder = embedder;

    private static readonly (AskIntent Intent, Regex Words)[] Telling =
    [
        (AskIntent.Crashes, R(@"\b(crash\w*|bsod|blue ?screen\w*|froze|freez\w*|hang\w*|hung|restart\w*|reboot\w*|shut ?down|shut off|turned off|black screen|not responding|stopped responding)\b")),
        (AskIntent.Changes, R(@"\b(install\w*|uninstall\w*|updat\w*|changed?|changes|removed?|driver update|new apps?|startup)\b")),
        (AskIntent.Temps, R(@"\b(temp\w*|hot|hotter|hottest|heat\w*|warm\w*|degrees|overheat\w*|thermal\w*|cool\w*)\b|°")),
        (AskIntent.Usage, R(@"\b(screen ?time|how long|hours?|time spent|last (time|played?|opened|used)|play ?time)\b")),
        (AskIntent.TopApps, R(@"\b(most used|most played|use the most|played the most|top (apps|games)|mostly)\b")),
        (AskIntent.Slow, R(@"\b(slow\w*|lag\w*|stutter\w*|sluggish|fps|frame ?rate|frames|choppy|hogging|unresponsive|performance)\b")),
        (AskIntent.Memory, R(@"\b(ram|memory)\b")),
        (AskIntent.Network, R(@"\b(internet|wi-?fi|network|download\w*|upload\w*|bandwidth|data usage|mbps|ping|disconnect\w*|connection)\b")),
        (AskIntent.Storage, R(@"\b(disk|drive|ssd|hdd|storage|space|full)\b")),
        (AskIntent.Fans, R(@"\b(fans?|rpm|noisy|loud|noise)\b")),
        (AskIntent.Health, R(@"\b(healthy?|summary|summar\w+|recap|overview|sum up|worry|alright|doing)\b")),
        (AskIntent.Specs, R(@"\b(specs?|specifications?|what (gpu|cpu|processor|graphics card|motherboard)|which (gpu|cpu|processor|graphics card|motherboard|driver)|version|do i have)\b")),
    ];

    // ── What it has learned from this user ──────────────────────────────

    /// <summary>A match this close to something learned is taken as the same question, whatever else it resembles.</summary>
    private const double LearnedSame = 0.90;

    /// <summary>A learned wording counts a little more than a built-in one: it is how this user asks.</summary>
    private const double LearnedBonus = 0.05;

    public const int MaxLearned = 400;

    private readonly List<(AskLearned Item, float[]? Vector)> _learned = [];

    private Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names the user gave apps (what they typed to the app's exe), from <see cref="AskMemory.Aliases"/>.</summary>
    public void SetAliases(IReadOnlyDictionary<string, string> aliases)
    {
        lock (_gate) _aliases = new(aliases, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What people call things, where it shares no word and no initials with the name ("vs code", "wow", "lol").</summary>
    private static readonly (string Short, string Name)[] Nicknames =
    [
        ("vs code", "visual studio code"), ("vscode", "visual studio code"), ("vsc", "visual studio code"), ("vs", "visual studio"), ("csgo", "counter-strike"),
        ("cs go", "counter-strike"), ("wow", "world of warcraft"), ("lol", "league of legends"), ("league", "league of legends"), ("cod", "call of duty"),
        ("warzone", "call of duty"), ("pubg", "playerunknown"), ("tf2", "team fortress 2"), ("mc", "minecraft"), ("ow", "overwatch"), ("ow2", "overwatch 2"),
        ("rdr2", "red dead redemption 2"), ("rdr", "red dead redemption"), ("gta", "grand theft auto"), ("gta 5", "grand theft auto v"), ("gta v", "grand theft auto v"),
        ("bg3", "baldur's gate 3"), ("cp2077", "cyberpunk 2077"), ("cyberpunk", "cyberpunk 2077"), ("val", "valorant"), ("fn", "fortnite"), ("rl", "rocket league"),
        ("edge", "microsoft edge"), ("word", "microsoft word"), ("excel", "microsoft excel"), ("teams", "microsoft teams"), ("ps", "photoshop"), ("obs", "obs studio"),
        ("yt", "youtube"), ("whatsapp", "whatsapp"), ("tg", "telegram"), ("notepad++", "notepad++"), ("file explorer", "explorer"), ("task manager", "taskmgr"),
    ];

    /// <summary>Wordings learned so far (see <see cref="Learn"/>).</summary>
    public IReadOnlyList<AskLearned> Learned { get { lock (_gate) return [.. _learned.Select(l => l.Item)]; } }

    /// <summary>Puts back what was learned in earlier sessions.</summary>
    public void SetLearned(IEnumerable<AskLearned> items)
    {
        lock (_gate)
        {
            _learned.Clear();
            _learned.AddRange(items.Select(i => (i, (float[]?)null)));
        }
    }

    /// <summary>
    /// Remembers that a question asked this way means <paramref name="intent"/>: the wording without its time and with
    /// its app as "this app", so the next question like it is understood, about any app and any day. A wording learned
    /// before is corrected, not doubled.
    /// </summary>
    public AskLearned? Learn(AskQuery q, AskIntent intent, bool why)
    {
        string text = q.Generic.Trim();
        if (text.Length < 2 || intent is AskIntent.None) return null;
        var item = new AskLearned(text, intent, why);
        lock (_gate)
        {
            _learned.RemoveAll(l => l.Item.Text == text);
            _learned.Add((item, null));
            if (_learned.Count > MaxLearned) _learned.RemoveAt(0);
        }
        return item;
    }

    public void Forget()
    {
        lock (_gate) _learned.Clear();
    }

    private List<(AskLearned Item, float[] Vector)> LearnedVectors()
    {
        if (_embedder is null) return [];
        lock (_gate)
        {
            var missing = _learned.Select((l, i) => (l, i)).Where(x => x.l.Vector is null).ToList();
            foreach (var chunk in missing.Chunk(32))
            {
                var vectors = _embedder.Embed([.. chunk.Select(x => x.l.Item.Text)]);
                for (int k = 0; k < chunk.Length; k++) _learned[chunk[k].i] = (chunk[k].l.Item, vectors[k]);
            }
            return [.. _learned.Select(l => (l.Item, l.Vector!))];
        }
    }

    /// <summary>
    /// A message cut into the questions it asks: at a question mark, or at "and" / "also" before a word that starts a
    /// question ("…yesterday and how hot was it"). Up to three; one that is two words or less isn't a question of its own.
    /// </summary>
    public static List<string> Split(string text)
    {
        var parts = Splitter().Split(text.Trim()).Select(p => LeadIn().Replace(p.Trim(' ', ',', ';'), "")).Where(p => p.Length > 0).ToList();
        return parts.Count is < 2 or > 3 || parts.Any(p => Word().Matches(p.ToLowerInvariant()).Count < 2) ? [text] : parts;
    }

    [GeneratedRegex(@"^(?:and also|and then|also|and|plus)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"(?<=\?)\s+|\s*[,;]?\s+(?:and also|and then|also|and|plus)\s+(?=(?:why|what|whats|what's|how|when|did|is|was|which|who|are|were|has|have|any|show)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Splitter();

    private bool Knows(string word)
    {
        try { return _embedder!.IsWord(word); }
        catch { return true; }
    }

    [GeneratedRegex(@"\b(play(ed|ing)?|launch(ed)?) ")]
    private static partial Regex PlayVerb();

    [GeneratedRegex(@"\bwhen did (i|it|my pc|the pc|the computer) (turn|switch|shut|power|go)\w* ?(it |my pc |the pc |the computer )?(off|down)\b|\bwhen did i (stop|log off|sign off|finish)\b")]
    private static partial Regex TurnedOff();

    /// <summary>Embeds the examples once (a fraction of a second), on first use.</summary>
    private float[][]? Examples()
    {
        if (_embedder is null) return null;
        lock (_gate)
        {
            if (_examples is not null) return _examples;
            var list = new List<float[]>(AskExamples.All.Length);
            foreach (var chunk in AskExamples.All.Chunk(32)) list.AddRange(_embedder.Embed([.. chunk.Select(e => e.Text)]));
            return _examples = [.. list];
        }
    }

    /// <summary>Loads the model's side of things ahead of the first question.</summary>
    public void WarmUp()
    {
        try { Examples(); }
        catch (Exception ex) { Log.Error("ask", ex); }
    }

    public AskQuery Read(string text, DateTime now, IReadOnlyList<AskApp> apps, AskContext? context = null)
    {
        text = FixTypos(Spaces().Replace(text.Trim(), " "), apps, _embedder);
        string lower = text.ToLowerInvariant();

        var (period, rest) = AskTime.Parse(text, now);
        AskPeriod? against = null;
        if (AskTime.ParseTwo(text, now) is { } two) (period, against, rest) = (two.Subject, two.Against, two.Left);
        var (app, withoutApp, generic) = FindApp(rest, apps);
        // A name this user taught, or a common nickname, anywhere in the question.
        if (app is null && Called(rest, apps) is { } called)
        {
            string stand = called.App.Category == AppCategory.Game ? "this game" : "this app";
            (app, withoutApp, generic) = (called.App, Spaces().Replace(rest.Remove(called.At, called.Length), " ").Trim(), rest.Remove(called.At, called.Length).Insert(called.At, stand));
        }
        string? unknown = null;
        if (app is null && Named(rest) is { } name)
        {
            if (Loosely(name, apps) is { } meant)
            {
                int at = rest.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                string stand = meant.Category == AppCategory.Game ? "this game" : "this app";
                (app, withoutApp, generic) = (meant, Spaces().Replace(rest.Remove(at, name.Length), " ").Trim(), rest.Remove(at, name.Length).Insert(at, stand));
            }
            else if (PlayVerb().IsMatch(rest.ToLowerInvariant()) || _embedder is null || !Word().Matches(name.ToLowerInvariant()).All(w => Knows(w.Value))) unknown = name;
        }
        var part = PartOf(lower);
        string? drive = DriveLetter().Match(lower) is { Success: true } d ? (d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value).ToUpperInvariant() : null;
        if (drive is not null && part == AskPart.None) part = AskPart.Drive;

        // Two wordings of the same question: as asked, and with its time and app out of the way.
        var (intent, score, second, askedWhy, learned) = Score([lower, generic.ToLowerInvariant()], lower);

        bool why = WhyWords().IsMatch(lower) || (askedWhy && !ListWords().IsMatch(lower));
        bool howTo = HowToWords().IsMatch(lower);
        bool games = app is null && GameWords().IsMatch(lower);
        bool last = LastWords().IsMatch(lower);

        // Two apps set side by side ("more Rematch or Dota?").
        AskApp? otherApp = app is not null && Versus2().IsMatch(lower) ? FindApp(withoutApp, [.. apps.Where(x => x.Id != app.Id)]).App : null;

        // What needs no model: next to nothing typed, a bare yes or no, a goodbye, praise, an insult, or only a time.
        int letters = lower.Count(char.IsLetter);
        bool rude = RudeWords().IsMatch(lower);
        if (!learned)
        {
            rude = rude && (Words(lower) <= 4 || lower.Contains(" you"));
            if (letters < 2 || YesNo().IsMatch(lower)) (intent, score) = (AskIntent.None, 0);
            else if (rude) (intent, score) = (AskIntent.Other, 1);
            else if (Orders().IsMatch(lower)) (intent, score) = (AskIntent.Other, 1); // "write me…", "translate…": a job for another kind of assistant
            else if (ByeWords().IsMatch(lower) || PraiseWords().IsMatch(lower)) (intent, score) = (AskIntent.Thanks, 1);
            else if (TurnedOff().IsMatch(lower)) (intent, score) = (AskIntent.Usage, 1); // "when did I turn it off": a time of day, not a crash
        }

        // Which app works a part hardest, and which game runs hottest: asked in too many ways to leave to likeness.
        bool topBy = !learned && TopByWords().IsMatch(lower) && part is AskPart.Cpu or AskPart.Gpu;
        if (topBy) (intent, score) = (AskIntent.Slow, 1);
        else if (!learned && HeatRankWords().IsMatch(lower)) (intent, score) = (AskIntent.Temps, 1);

        // Only a time typed ("yesterday?"): by itself, how the PC did then; in a conversation, the same question about that time.
        bool barePeriod = !learned && period is not null && app is null && Word().Matches(Bare().Replace(rest.ToLowerInvariant(), " ")).Count == 0;
        if (barePeriod) (intent, score) = context is { Intent: not AskIntent.None } ? (AskIntent.None, 0) : (AskIntent.Health, 1);
        // Two times and nothing else to go on ("today vs yesterday"): how the PC did in each.
        if (!learned && against is not null && (score < Sure || intent is AskIntent.Other)) (intent, score) = (AskIntent.Health, 1);

        // A reading by name ("average CPU load", "GPU power draw") is read by rule: its words leave no doubt.
        var (metric, aggregate) = MetricOf(lower, part);
        if (metric != AskMetric.None && !topBy && !learned && !howTo && intent is not (AskIntent.Hello or AskIntent.Thanks or AskIntent.Who or AskIntent.Help))
            (intent, score) = (AskIntent.Metric, 1);

        var q = new AskQuery(text)
        {
            Intent = score >= Sure ? intent : AskIntent.None, Score = score, Second = second, Period = period, Against = against,
            Metric = metric, Aggregate = aggregate, Now = NowWords().IsMatch(lower), Rude = rude, UnknownApp = unknown, TopBy = topBy, BarePeriod = barePeriod, OtherAppId = otherApp?.Id, OtherApp = otherApp?.Name,
            Backed = Telling.Any(t => t.Intent == intent && t.Words.IsMatch(lower)),
            AppId = app?.Id, App = app?.Name, Part = part, Drive = drive, Why = why, Last = last, Games = games, HowTo = howTo && !learned,
            Generic = generic.ToLowerInvariant(),
            Rest = Spaces().Replace(Filler().Replace(withoutApp.ToLowerInvariant(), " "), " ").Trim(),
        };
        if (q.Intent == AskIntent.None && intent != AskIntent.None) q = q with { Second = intent == AskIntent.Other ? second : intent };

        return context is null ? q : FollowUp(q, lower, context, now);
    }

    /// <summary>
    /// A question that only makes sense after the one before: it starts with "and" or "what about", or it is little
    /// more than a time, an app or a part. It takes over what the last one asked, with whatever it names changed.
    /// </summary>
    /// <summary>
    /// What a question takes from the one before. A conversation stays on its time: a question that names none is
    /// about the time just talked about. And a short or leaning question ("and the CPU?", "who used it?", "why?",
    /// "yesterday?") takes the topic too, changing only what it names.
    /// </summary>
    private static AskQuery FollowUp(AskQuery q, string lower, AskContext context, DateTime now)
    {
        if (context.Intent == AskIntent.None) return q;
        bool hasSlot = q.Period is not null || q.AppId is not null || q.Part != AskPart.None;
        bool connector = Connector().IsMatch(lower);
        bool bareWhy = BareWhy().IsMatch(lower);
        int words = Words(lower);
        bool smallTalk = q.Intent is AskIntent.Hello or AskIntent.Thanks or AskIntent.Help or AskIntent.Who;
        // Small talk stands on its own, unless it only looks like it: "what about Saturday" isn't a call for help, nor "why?" a "who are you".
        if ((smallTalk && !bareWhy && !q.BarePeriod && !(hasSlot && (connector || q.Score < 0.7)) && !(q.Score < 0.7 && Pronoun().IsMatch(lower))) || q.Rude) return q;
        if (smallTalk) q = q with { Intent = AskIntent.None, Score = 0 };
        q = q with { Problem = context.Problem, ProblemApp = context.ProblemApp };

        // After a crash was explained, "how do I fix it?" is about that crash.
        if (q.HowTo && context.Problem is not null) return q with { Period = q.Period ?? context.Period };

        // "When did that start?", "how often does that happen?": "that" is the problem just explained.
        if (context.Problem is not null && ThatProblem().IsMatch(lower))
            return q with { Intent = AskIntent.Crashes, Score = 1, History = true, Why = false, AppId = context.AppId, App = context.App, FollowsUp = true, Anchor = context.Period };

        // "Any crashes since then?", "and after that?": from the day just talked about until now.
        if (context.Period is { IsReal: true } then && q.Period is null && SinceThen().IsMatch(lower))
            q = q with { Period = new AskPeriod(then.From, now.Date.AddDays(1), Reports.ReportRange.Custom, $"since {Label(then)}") };

        // "And before that?", "the one before": the problem before the one just explained.
        if (context.Intent == AskIntent.Crashes && context.Period is { IsReal: true } at && BeforeThat().IsMatch(lower))
            return q with
            {
                Intent = AskIntent.Crashes, Score = 1, Why = context.Why, Last = true, FollowsUp = true,
                Period = new AskPeriod(new DateTime(2000, 1, 1), at.From, Reports.ReportRange.Custom, $"before {Label(at)}"),
            };

        // A reading named with its part left unsaid ("and how much power?" after a question about the GPU).
        if (q.Metric is AskMetric.None or AskMetric.Power && q.Part == AskPart.None && context.Part is AskPart.Cpu or AskPart.Gpu && MetricOf(lower, context.Part) is { Metric: not AskMetric.None } named)
            q = q with { Intent = AskIntent.Metric, Score = 1, Metric = named.Metric, Aggregate = named.Aggregate, Part = context.Part };

        // What makes a question lean on the last one: it says so ("and…", "what about…"), it is only a "why", it is a few
        // words that are no question alone, or it points back ("who used it?") without a telling word of its own.
        bool pointsBack = Pronoun().IsMatch(lower) && !q.Backed && q.Score < 0.8 && !q.HowTo && q.Metric == AskMetric.None;
        bool fragment = words <= 3 && (q.Intent is AskIntent.None or AskIntent.Other || q.Score < 0.6) && q.Metric == AskMetric.None;
        bool leans = bareWhy || pointsBack || fragment || (q.Intent == AskIntent.None && hasSlot && words <= 5)
            || (connector && q.Score < 0.9 && q.Metric == AskMetric.None && (hasSlot || q.Intent == AskIntent.None || words <= 4));

        // "Was it the driver?", "was that heat?": back to why it crashed, where the evidence is laid out.
        if (pointsBack && context.Problem is not null && context.Period is { IsReal: true } crashDay && q.Period is null)
            return q with { Intent = AskIntent.Crashes, Score = 1, Why = true, Period = crashDay, FollowsUp = true };

        AskQuery Inherit(AskQuery x, AskIntent intent) => x with
        {
            Alternative = x.Intent is not (AskIntent.None or AskIntent.Other or AskIntent.Hello or AskIntent.Thanks or AskIntent.Help or AskIntent.Who) && x.Intent != intent && x.Score >= Sure
                ? x.Intent : AskIntent.None,
            Intent = intent, Score = 1, AppId = x.AppId ?? context.AppId, App = x.App ?? context.App, Part = x.Part != AskPart.None ? x.Part : context.Part,
            Why = x.Why || bareWhy || context.Why, FollowsUp = true, Metric = x.Metric != AskMetric.None ? x.Metric : context.Metric,
        };

        // "And at night?", "in the morning?" with no day named: that part of the day talked about (not of today or last night).
        if (context.Period is { IsDay: true } dayOn && DayPart().Match(lower) is { Success: true } partOf && !NamesDay().IsMatch(lower)
            && (q.Period is null || q.Period.Range == Reports.ReportRange.Custom) && (leans || words <= 5))
            return Inherit(q with { Period = null }, leans || q.Intent is AskIntent.None or AskIntent.Health ? context.Intent : q.Intent)
                with { Period = AskTime.PartOfDay(dayOn.From, partOf.Groups[1].Value, now) };

        if (context.Period is { IsReal: true } was && q.Period is null)
        {
            // "the day before", "the week after": counted from the time of the question before; "compared to the day before": set against it.
            if (Shift().Match(lower) is { Success: true } shift && Shifted(was, shift, now) is { } moved && (leans || q.Intent == AskIntent.None || connector || words <= 6))
                return Versus().IsMatch(lower) ? Inherit(q, context.Intent) with { Period = was, Against = moved } : Inherit(q, context.Intent) with { Period = moved };
            // "and at night?", "in the morning?": that part of the day talked about.
            if (was.IsDay && DayPart().Match(lower) is { Success: true } part && (leans || words <= 5))
                return Inherit(q, context.Intent) with { Period = AskTime.PartOfDay(was.From, part.Groups[1].Value, now) };
        }

        // The time: named now, or the one the conversation is on. After a crash, "what changed" means in the days leading up to it.
        var period = q.Period;
        if (period is null && context.Period is { IsReal: true } on && !q.Now && !q.Last)
            period = context.Problem is not null && (leans ? context.Intent : q.Intent) == AskIntent.Changes
                ? new AskPeriod(on.From.AddDays(-14), on.To, Reports.ReportRange.Custom, $"in the 14 days up to {Label(on)}")
                : on;
        // …and the conversation stays on the crash's own day.
        if (period is not null && period != context.Period && q.Period is null) q = q with { Anchor = context.Period };

        if (!leans)
        {
            // It stands as its own question; it only keeps the conversation's time (how the PC is doing asks about now unless told).
            bool keepsTime = q.Period is null && period is not null && (q.Intent is AskIntent.Crashes or AskIntent.Changes or AskIntent.Temps or AskIntent.Usage or AskIntent.TopApps
                or AskIntent.Slow or AskIntent.Memory or AskIntent.Network or AskIntent.Fans or AskIntent.Metric);
            return keepsTime ? q with { Period = period, CarriedTime = true } : q;
        }

        // A part named in a follow-up to a question about the whole PC turns it to that part's own answer.
        var intent = context.Intent;
        if (q.Part != AskPart.None && context.Intent is AskIntent.Health or AskIntent.Temps or AskIntent.Memory or AskIntent.Fans or AskIntent.Storage or AskIntent.Network)
            intent = q.Part switch
            {
                AskPart.Cpu or AskPart.Gpu => AskIntent.Temps,
                AskPart.Memory => AskIntent.Memory,
                AskPart.Fans => AskIntent.Fans,
                AskPart.Drive => AskIntent.Storage,
                AskPart.Network => AskIntent.Network,
                _ => intent,
            };
        return Inherit(q, intent) with { Period = period, CarriedTime = q.Period is null && period is not null };

        static string Label(AskPeriod p) => p.Label.StartsWith("on ", StringComparison.Ordinal) || p.Label.StartsWith("in ", StringComparison.Ordinal) ? p.Label[3..] : p.Label;
    }

    [GeneratedRegex(@"\b(or|than|vs\.?|versus|against|compared)\b")]
    private static partial Regex Versus2();

    [GeneratedRegex(@"\b(since|after) (then|that|it|this)\b|\bfrom then on\b")]
    private static partial Regex SinceThen();

    [GeneratedRegex(@"\b(it|that|them|those|these|this one|that one)\b")]
    private static partial Regex Pronoun();

    [GeneratedRegex(@"^(and |what about |how about )?(the )?(one |time |crash |problem )?(before|prior to) (that|it|this)\b|\b(the )?(previous|earlier|last) (one|crash|time|problem)\b|\bbefore that\b")]
    private static partial Regex BeforeThat();

    [GeneratedRegex(@"\b(compared|compare|than|vs\.?|versus|against)\b")]
    private static partial Regex Versus();

    [GeneratedRegex(@"\b(today|tonight|yesterday|last night|this (morning|afternoon|evening)|tomorrow|mon|tue|wed|thu|fri|sat|sun|\d)")]
    private static partial Regex NamesDay();

    [GeneratedRegex(@"\b(?:at|in the|that|during the|the)\s+(morning|afternoon|evening|night)\b")]
    private static partial Regex DayPart();

    /// <summary>A period one step before or after another, of the same length (a day, a week, a month, a year).</summary>
    private static AskPeriod? Shifted(AskPeriod p, Match m, DateTime now)
    {
        int step = m.Groups["dir"].Value is "before" or "earlier" or "previous" ? -1 : 1;
        return m.Groups["unit"].Value switch
        {
            "day" or "night" => AskTime.Day(p.From.Date.AddDays(step), now),
            "week" => AskTime.Week(p.From.Date.AddDays(7 * step), now),
            "month" => AskTime.Month(p.From.Date.AddMonths(step), now),
            "year" => AskTime.Year(p.From.Year + step, now),
            _ => null,
        };
    }

    [GeneratedRegex(@"\b(?:the )?(?<unit>day|night|week|month|year) (?<dir>before|after|earlier|later)\b|\b(?:the )?(?<dir>previous|next|following) (?<unit>day|week|month|year)\b")]
    private static partial Regex Shift();

    private (AskIntent Intent, double Score, AskIntent Second, bool Why, bool Learned) Score(string[] wordings, string lower)
    {
        (AskLearned Item, double Score)? taught = null;
        var scores = new Dictionary<AskIntent, (double Score, bool Why)>();
        float[][]? examples = null;
        float[][]? asked = null;
        try
        {
            examples = Examples();
            if (examples is not null) asked = _embedder!.Embed([.. wordings.Distinct()]);
        }
        catch (Exception ex)
        {
            Log.Error("ask", ex);
        }

        if (examples is not null && asked is not null)
        {
            for (int i = 0; i < examples.Length; i++)
            {
                var example = AskExamples.All[i];
                double best = asked.Max(v => Dot(v, examples[i]));
                if (!scores.TryGetValue(example.Intent, out var have) || best > have.Score) scores[example.Intent] = (best, example.Why);
            }
            // The user's own wordings, taught by picking what a question meant.
            foreach (var (item, vector) in LearnedVectors())
            {
                double best = asked.Max(v => Dot(v, vector));
                if (taught is null || best > taught.Value.Score) taught = (item, best);
                if (!scores.TryGetValue(item.Intent, out var have) || best + LearnedBonus > have.Score) scores[item.Intent] = (best + LearnedBonus, item.Why);
            }
        }
        else
        {
            // Without the model, a learned wording still counts when it is asked again word for word.
            lock (_gate)
                if (_learned.Select(l => l.Item).FirstOrDefault(l => wordings.Contains(l.Text)) is { } same) taught = (same, 1);
        }
        if (taught is { Score: >= LearnedSame } t) return (t.Item.Intent, 1, AskIntent.None, t.Item.Why, true);

        // Telling words: on their own they carry a question when there's no model; with it, they settle close calls.
        foreach (var (intent, words) in Telling)
        {
            if (!words.IsMatch(lower)) continue;
            scores.TryGetValue(intent, out var have);
            scores[intent] = examples is null ? (Sure + Boost, have.Why) : (have.Score + Boost, have.Why);
        }
        if (examples is null && scores.Count > 1)
        {
            // Without the model, several telling words is a tie: the first in the table's order wins, a little unsure.
            var first = Telling.First(t => scores.ContainsKey(t.Intent)).Intent;
            scores[first] = (Sure + Boost + 0.01, scores[first].Why);
        }
        if (scores.Count == 0) return (AskIntent.None, 0, AskIntent.None, false, false);

        var ranked = scores.OrderByDescending(s => s.Value.Score).ToList();
        var top = ranked[0];
        var second = ranked.Skip(1).FirstOrDefault(s => s.Key is not (AskIntent.Other or AskIntent.Hello or AskIntent.Thanks or AskIntent.Who or AskIntent.Help)).Key;
        return (top.Key, Math.Min(1, top.Value.Score), second, top.Value.Why, false);
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    // ── The app a question names ────────────────────────────────────────

    /// <summary>The words questions here are made of: what a mistyped word is set right to, and never an app's name by themselves.</summary>
    private static readonly string[] Vocabulary =
    [
        "crash", "crashed", "crashes", "crashing", "yesterday", "today", "tonight", "temperature", "temperatures", "driver", "drivers", "memory", "internet",
        "storage", "update", "updated", "updates", "install", "installed", "uninstalled", "graphics", "processor", "overheating", "download", "downloaded",
        "changed", "changes", "laggy", "lagging", "stutter", "stuttering", "freeze", "freezing", "frozen", "restart", "restarted", "shutdown", "screen",
        "network", "windows", "week", "month", "played", "playing", "average", "highest", "lowest", "usage", "power", "service", "list", "host", "fans",
        "slow", "games", "apps", "morning", "evening", "night", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "what", "when",
        "long", "much", "many", "used", "using", "data", "disk", "drive", "space", "full", "load", "heat", "last", "this", "more", "less", "than",
    ];

    /// <summary>
    /// Sets right a word one slip away from one of the words questions are made of ("crahs", "yestreday", "temprature"):
    /// a letter missing, extra, wrong, or two swapped. Words of five letters or more only, and never a word of an
    /// app's name (a game called "Crasher" stays that).
    /// </summary>
    internal static string FixTypos(string text, IReadOnlyList<AskApp> apps, IAskEmbedder? words)
    {
        // The model's vocabulary tells a slip from a real word ("drives" is not a mistyped "driver"); without it, nothing is touched.
        if (words is null) return text;
        var known = new HashSet<string>(Vocabulary, StringComparer.Ordinal);
        HashSet<string>? names = null;
        return TypoWord().Replace(text, m =>
        {
            string word = m.Value.ToLowerInvariant();
            if (word.Length < 5 || known.Contains(word) || Common.Contains(word)) return m.Value;
            try
            {
                if (words.IsWord(word)) return m.Value;
            }
            catch
            {
                return m.Value;
            }
            names ??= [.. apps.SelectMany(a => Word().Matches(a.Name.ToLowerInvariant()).Select(w => w.Value).Append(Path.GetFileNameWithoutExtension(a.Exe).ToLowerInvariant()))];
            if (names.Contains(word)) return m.Value;
            return Vocabulary.FirstOrDefault(v => v.Length >= 5 && Math.Abs(v.Length - word.Length) <= 1 && OneSlip(word, v)) ?? m.Value;
        });
    }

    /// <summary>Whether two words differ by one edit: a letter added, dropped or changed, or two neighbours swapped.</summary>
    private static bool OneSlip(string a, string b)
    {
        if (a == b) return false;
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        if (a.Length == b.Length)
            return a[(i + 1)..] == b[(i + 1)..]                                                    // one changed
                || (i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..] == b[(i + 2)..]); // two swapped
        var (longer, shorter) = a.Length > b.Length ? (a, b) : (b, a);
        return longer[(i + 1)..] == shorter[i..];                                                 // one added or dropped
    }

    [GeneratedRegex(@"[A-Za-z]{5,}")]
    private static partial Regex TypoWord();

    [GeneratedRegex(@"^(please )?(write|compose|draw|paint|generate|translate|sing|play|calculate|solve|code|program|summari[sz]e this|rewrite|proofread|imagine|pretend|roleplay|act as|ignore (all |your |the )?(previous|prior|above))\b")]
    private static partial Regex Orders();

    /// <summary>The little words around a time typed by itself ("and on Friday?", "what about last week").</summary>
    [GeneratedRegex(@"\b(and|on|in|at|for|the|about|what|how|so|then|please|ok|okay|riggy)\b")]
    private static partial Regex Bare();

    [GeneratedRegex(@"^(yes|yeah|yep|yup|no|nope|nah|maybe|sure|k|kk)[.!?]*$")]
    private static partial Regex YesNo();

    [GeneratedRegex(@"^(bye|goodbye|good bye|bye bye|see (you|ya)( later)?|cya|later|good night|gn)[.! ]*(riggy)?[.!]*$")]
    private static partial Regex ByeWords();

    [GeneratedRegex(@"^(good|great|nice|best|smart|clever|cool) (bot|job|work|one|boy|girl)|^(well done|you('re| are) (great|awesome|good|the best|smart|helpful)|love (you|it|this)|that helped|i love you)")]
    private static partial Regex PraiseWords();

    [GeneratedRegex(@"\b(fuck|fck|f\*+k|shit|stupid|idiot|dumb|useless|suck|sucks|trash bot|garbage bot|bad bot|worst|hate you|shut up|stfu|moron|crap)\b")]
    private static partial Regex RudeWords();

    [GeneratedRegex(@"\b(what|which|who)\b.*\b(most|heaviest|highest|hardest|hogging|hogs?|eating)\b|\b(most|heaviest) (cpu|gpu)\b|\b(cpu|gpu|processor) (hog|hogs|hungry)\b")]
    private static partial Regex TopByWords();

    [GeneratedRegex(@"\b(which|what) (game|app|program)s?\b.*\b(hottest|hot|heats?|warm\w*|demanding|heaviest|hardest)\b|\b(hottest|heaviest|most demanding) (game|app)\b")]
    private static partial Regex HeatRankWords();

    [GeneratedRegex(@"\b(right now|now|currently|current|at the moment|this minute|atm)\b")]
    private static partial Regex NowWords();

    /// <summary>Words that are part of asking, not of an app's name: an app called "Settings" isn't meant by "which settings changed".</summary>
    private static readonly HashSet<string> Common = BuildCommon();

    private static HashSet<string> BuildCommon()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in AskExamples.All)
            foreach (Match w in Word().Matches(e.Text)) set.Add(w.Value);
        foreach (string w in Vocabulary) set.Add(w);
        foreach (string w in "google microsoft adobe launcher client studio player desktop service helper manager center centre control panel edition world call duty time last data system settings explorer tool tools file files task photos mail store security home live online web video music calculator notes clock camera maps news weather terminal remote setup installer update updater beta free pro plus lite classic remastered definitive ultimate deluxe game games app apps program programs software".Split(' '))
            set.Add(w);
        return set;
    }

    /// <summary>
    /// The app named in a question, the question without it, and the question with "this game" or "this app" in its
    /// place. A full name or an exe name always counts; one word of a longer name ("cyberpunk" for Cyberpunk 2077) only
    /// when it isn't an everyday word. The longest match wins, then the app used most.
    /// </summary>
    internal static (AskApp? App, string Without, string Generic) FindApp(string text, IReadOnlyList<AskApp> apps)
    {
        string lower = text.ToLowerInvariant();
        (AskApp App, int Index, int Length, int Rank)? best = null;
        foreach (var app in apps)
        {
            string name = app.Name.ToLowerInvariant();
            string exe = Path.GetFileNameWithoutExtension(app.Exe).ToLowerInvariant();
            Consider(app, name, 3);
            if (exe != name) Consider(app, exe, 2);
            var words = Word().Matches(name).Select(w => w.Value).ToList();
            if (words.Count > 1)
                foreach (string w in words)
                    // One word of a name only for an app that is really used: nobody means "Network List Service" by "network".
                    if (w.Length >= 4 && app.ActiveSec >= 600 && !Common.Contains(w) && !w.All(char.IsDigit)) Consider(app, w, 1);
        }
        if (best is not { } b) return (null, text, text);
        string stand = b.App.Category == AppCategory.Game ? "this game" : "this app";
        return (b.App, Spaces().Replace(text.Remove(b.Index, b.Length), " ").Trim(), text.Remove(b.Index, b.Length).Insert(b.Index, stand));

        void Consider(AskApp app, string key, int rank)
        {
            if (key.Length < 3 || (rank > 1 && !key.Contains(' ') && Common.Contains(key))) return;
            int at = IndexOfWord(lower, key);
            if (at < 0) return;
            // Longer first, then a fuller kind of match, then the app used more.
            if (best is not { } have || key.Length > have.Length || (key.Length == have.Length && (rank > have.Rank || (rank == have.Rank && app.ActiveSec > have.App.ActiveSec))))
                best = (app, at, key.Length, rank);
        }
    }

    /// <summary>Words that follow "play" or "use" without being an app: the PC and its parts, and words of time and amount.</summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "pc", "computer", "it", "them", "this", "that", "these", "those", "the", "my", "a", "an", "rig", "machine", "laptop", "desktop", "system", "internet", "data", "memory", "ram",
        "cpu", "gpu", "disk", "drive", "storage", "space", "fans", "fan", "games", "game", "apps", "app", "anything", "something", "everything", "most", "more", "less",
        "much", "many", "long", "last", "first", "usually", "today", "yesterday", "lately", "recently", "now", "ever", "total", "all", "any", "so", "too", "up", "on", "in",
        "at", "for", "with", "and", "or", "of", "to", "me", "i", "we", "you", "is", "was", "be", "been", "online", "offline", "there", "here", "again", "before", "better",
        "worse", "bad", "badly", "well", "fine", "hot", "hotter", "slow", "slower", "fast", "faster", "laggy", "open", "running", "power", "electricity", "time", "day",
        "week", "month", "year", "night", "morning", "evening", "graphics", "card", "processor", "screen", "windows", "network", "wifi", "bandwidth", "a lot", "lot",
        "browser", "browsers", "launcher", "editor", "player", "program", "programs", "software", "tool", "tools", "thing", "things", "stuff", "session", "sessions",
    };

    /// <summary>
    /// The name an app would have by its place in the sentence: after "play" or "use", before "crash", after "does".
    /// Null when what stands there is the PC, a part, or a plain word.
    /// </summary>
    internal static string? Named(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach (var regex in new[] { AfterVerb(), BeforeVerb(), DoesVerb() })
        {
            var m = regex.Match(lower);
            if (!m.Success) continue;
            var words = Word().Matches(m.Groups["x"].Value).Select(w => w.Value).ToList();
            while (words.Count > 0 && NotNames.Contains(words[0])) words.RemoveAt(0);
            while (words.Count > 0 && NotNames.Contains(words[^1])) words.RemoveAt(words.Count - 1);
            // A name has no word of asking in it ("how many times did" before "crash" is not one), and isn't one letter.
            if (words.Count is 0 or > 4 || words.Any(w => NotNames.Contains(w) || Asking.Contains(w)) || words.All(w => w.Length < 2 || Vocabulary.Contains(w))) continue;
            string name = string.Join(' ', words);
            int at = lower.IndexOf(name, m.Groups["x"].Index, StringComparison.Ordinal);
            if (at >= 0) return text.Substring(at, name.Length);
        }
        return null;
    }

    private static readonly HashSet<string> Asking = new(StringComparer.Ordinal)
    {
        "how", "what", "why", "when", "which", "who", "where", "did", "does", "do", "is", "was", "were", "are", "has", "have", "had", "times", "often", "number",
        "count", "keep", "keeps", "kept", "still", "just", "always", "never", "also", "ever", "can", "could", "would", "should", "will", "not", "no", "s", "t",
        "friends", "background", "front", "use", "play", "one", "two", "some", "few", "lot", "lots", "bit", "while", "since", "until", "than", "then", "if",
    };

    /// <summary>
    /// The app meant by a name given in its place in the sentence: its name or exe in full, the start of its name, a
    /// whole word of it ("terminal", "explorer", "code"), or its initials ("cs" for Counter-Strike). The most used one.
    /// </summary>
    /// <summary>An app named by what this user calls it (taught) or by a common nickname, as a whole word; the longest such name.</summary>
    private (AskApp App, int At, int Length)? Called(string text, IReadOnlyList<AskApp> apps)
    {
        string lower = text.ToLowerInvariant();
        (AskApp App, int At, int Length)? best = null;
        void Try(string name, AskApp? app)
        {
            if (app is null || IndexOfWord(lower, name) is not (>= 0 and var at)) return;
            if (best is null || name.Length > best.Value.Length) best = (app, at, name.Length);
        }
        Dictionary<string, string> aliases;
        lock (_gate) aliases = _aliases;
        foreach (var (name, exe) in aliases)
            Try(name.ToLowerInvariant(), apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase)));
        foreach (var (name, full) in Nicknames)
            Try(name, apps.Where(a => a.Name.Contains(full, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(a.Exe).Equals(full, StringComparison.OrdinalIgnoreCase))
                .MaxBy(a => a.ActiveSec));
        // Two letters are a word of their own too often ("ps", "ow", "mc", "vs"): only where the sentence places an app there.
        return best is { Length: <= 2 } shortOne && !(Named(text) is { } placed && placed.Equals(text.Substring(shortOne.At, shortOne.Length), StringComparison.OrdinalIgnoreCase)) ? null : best;
    }

    internal static AskApp? Loosely(string name, IReadOnlyList<AskApp> apps)
    {
        string key = name.ToLowerInvariant();
        (AskApp App, int Rank)? best = null;
        foreach (var app in apps)
        {
            string full = app.Name.ToLowerInvariant(), exe = Path.GetFileNameWithoutExtension(app.Exe).ToLowerInvariant();
            var words = Word().Matches(full).Select(w => w.Value).ToList();
            string initials = string.Concat(words.Where(w => !w.All(char.IsDigit)).Select(w => w[0]));
            int rank = full == key || exe == key ? 5
                : full.StartsWith(key + " ", StringComparison.Ordinal) ? 4
                : IndexOfWord(full, key) >= 0 ? 3
                : key.Length >= 2 && initials.Length >= 2 && (initials == key || initials + string.Concat(words.Where(w => w.All(char.IsDigit))) == key) ? 2
                : 0;
            if (rank == 0) continue;
            if (best is not { } have || rank > have.Rank || (rank == have.Rank && app.ActiveSec > have.App.ActiveSec)) best = (app, rank);
        }
        return best?.App;
    }

    [GeneratedRegex(@"\b(?:play(?:ed|ing)?|us(?:e|ed|ing)|open(?:ed)?|launch(?:ed)?) (?<x>[a-z0-9][a-z0-9 :'.+-]{0,40}?)\s*(?=$|[?!,]| (?:crash|make|made|freeze|froze|lag|open|for|so|the most|more|less)\b)")]
    private static partial Regex AfterVerb();

    [GeneratedRegex(@"\b(?:does|did) (?<x>[a-z0-9][a-z0-9 :'.+-]{0,40}?) (?:use|make|draw|take|download|upload|need)\b")]
    private static partial Regex DoesVerb();

    [GeneratedRegex(@"^(?:why (?:did|does|is|was) |did |does |has |is |was |when did )?(?<x>[a-z0-9][a-z0-9 :'.+-]{0,40}?) (?:crash\w*|keeps? (?:crashing|freezing|dying)|froze|freez\w*|lag\w*|stutter\w*|is (?:slow|laggy)|runs? (?:hot|slow|badly))\b")]
    private static partial Regex BeforeVerb();

    private static int IndexOfWord(string text, string word)
    {
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            bool after = at + word.Length == text.Length || !char.IsLetterOrDigit(text[at + word.Length]);
            if (before && after) return at;
        }
        return -1;
    }

    /// <summary>
    /// The reading a question names, and which figure of it. Load, power, clock and hot spot are always this kind of
    /// question; a temperature or memory only when its average or lowest is asked for (their highs and "is it OK" have
    /// answers of their own).
    /// </summary>
    internal static (AskMetric Metric, AskAggregate Aggregate) MetricOf(string lower, AskPart part)
    {
        var aggregate = AverageWords().IsMatch(lower) ? AskAggregate.Average : LowestWords().IsMatch(lower) ? AskAggregate.Lowest
            : HighestWords().IsMatch(lower) ? AskAggregate.Highest : AskAggregate.Any;
        bool gpu = part == AskPart.Gpu, cpu = part == AskPart.Cpu;
        AskMetric metric =
            HotSpotWords().IsMatch(lower) ? AskMetric.HotSpot
            : PowerWords().IsMatch(lower) ? gpu ? AskMetric.GpuPower : cpu ? AskMetric.CpuPower : AskMetric.Power
            : VoltWords().IsMatch(lower) && (gpu || cpu) ? gpu ? AskMetric.GpuVolt : AskMetric.CpuVolt
            : ClockWords().IsMatch(lower) && (gpu || cpu) ? gpu ? AskMetric.GpuClock : AskMetric.CpuClock
            : LoadWords().IsMatch(lower) ? gpu ? AskMetric.GpuLoad : AskMetric.CpuLoad
            : aggregate is AskAggregate.Average or AskAggregate.Lowest && TempWords().IsMatch(lower) && (gpu || cpu) ? gpu ? AskMetric.GpuTemp : AskMetric.CpuTemp
            : aggregate is AskAggregate.Average or AskAggregate.Lowest && part == AskPart.Memory ? AskMetric.Ram
            : AskMetric.None;
        return (metric, aggregate);
    }

    [GeneratedRegex(@"\b(average|avg|mean|on average|typical(ly)?)\b")]
    private static partial Regex AverageWords();

    [GeneratedRegex(@"\b(lowest|minimum|min|least|coolest|coldest)\b")]
    private static partial Regex LowestWords();

    [GeneratedRegex(@"\b(highest|maximum|max|peak|most|top)\b")]
    private static partial Regex HighestWords();

    [GeneratedRegex(@"\bhot ?spot\b")]
    private static partial Regex HotSpotWords();

    [GeneratedRegex(@"\b(volts?|voltage|vcore)\b")]
    private static partial Regex VoltWords();

    [GeneratedRegex(@"\b(power draw|power use|power usage|power consumption|watts?|wattage|how much power)\b|\b(cpu|gpu|processor|graphics card) power\b")]
    private static partial Regex PowerWords();

    [GeneratedRegex(@"\b(clocks?|clock speed|frequency|mhz|ghz)\b")]
    private static partial Regex ClockWords();

    [GeneratedRegex(@"\b(cpu|processor|gpu|graphics card|graphics) (load|usage|utili[sz]ation|use)\b|\b(load|usage|utili[sz]ation) (of|on) (my |the )?(cpu|processor|gpu|graphics)|\bhow busy (was|is|were) (my |the )?(cpu|processor|gpu|graphics)")]
    private static partial Regex LoadWords();

    [GeneratedRegex(@"\b(temp\w*|degrees)\b")]
    private static partial Regex TempWords();

    [GeneratedRegex(@"\b(that|this|it|these|those)\b.*\b(start\w*|beg[aiu]n\w*|happen\w*|first|before|often|times)\b|\b(when|how often|how many times)\b.*\b(that|this|it|these|those)\b|^(how often|how many times|since when|when did (it|that|this) (start|begin|first))\b")]
    private static partial Regex ThatProblem();

    private static AskPart PartOf(string lower) =>
        CpuWords().IsMatch(lower) ? AskPart.Cpu
        : GpuWords().IsMatch(lower) ? AskPart.Gpu
        : MemoryWords().IsMatch(lower) ? AskPart.Memory
        : FanWords().IsMatch(lower) ? AskPart.Fans
        : DriveWords().IsMatch(lower) ? AskPart.Drive
        : NetWords().IsMatch(lower) ? AskPart.Network
        : AskPart.None;

    private static int Words(string text) => Word().Matches(text).Count;

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(why|what caused|what causes|what is causing|what's causing|whats causing|cause of|reason|explain|how come|what made|what went wrong|what's behind|whats behind|what is behind|what happened when)\b")]
    private static partial Regex WhyWords();

    [GeneratedRegex(@"^(did|has|have|had|were|was there|is there|are there|any|how many|how often|when|list|show|count)\b|\b(how many|how often|when was|when did)\b")]
    private static partial Regex ListWords();

    [GeneratedRegex(@"^(how (do|can|should|would|could) (i|you|we|one)\b|how to\b|what('s| is) the best way|can you (help me )?(fix|update|install|change|set|make|speed|clean)|should i (buy|get|upgrade|sell)|is it worth|what should i (buy|get))|\b(best settings|recommend\w*)\b")]
    private static partial Regex HowToWords();

    [GeneratedRegex(@"\b(gam(e|es|ing|ed)|play(ed|ing|time)?)\b")]
    private static partial Regex GameWords();

    [GeneratedRegex(@"\b(last time|when did i last|when was the last|last (played?|opened|used|launched|ran)|most recent(ly)?|latest)\b")]
    private static partial Regex LastWords();

    [GeneratedRegex(@"^(and|what about|how about|same for|also|ok and|okay and|but what about|what if|now)\b")]
    private static partial Regex Connector();

    [GeneratedRegex(@"^(but )?(why|how come|why is that|why so|why though|what caused (it|that)|what's the cause|explain( it| that)?)\??$")]
    private static partial Regex BareWhy();

    [GeneratedRegex(@"\b(then|that day|that night|that time|at the time|at that time|around then|when (it|that) happened|before (it|that)( happened)?|back then)\b")]
    private static partial Regex ThenWords();

    [GeneratedRegex(@"\b(cpu|processor|ryzen|core i\d|threadripper)\b")]
    private static partial Regex CpuWords();

    [GeneratedRegex(@"\b(gpu|graphics card|video card|graphics|rtx|gtx|radeon|geforce|vram)\b")]
    private static partial Regex GpuWords();

    [GeneratedRegex(@"\b(ram|memory)\b")]
    private static partial Regex MemoryWords();

    [GeneratedRegex(@"\bfans?\b")]
    private static partial Regex FanWords();

    [GeneratedRegex(@"\b(drives?|disks?|ssd|hdd|nvme|storage|disk space|free space)\b")]
    private static partial Regex DriveWords();

    [GeneratedRegex(@"\b(internet|network|wi-?fi|ethernet|bandwidth|download\w*|upload\w*)\b")]
    private static partial Regex NetWords();

    [GeneratedRegex(@"\b([a-z])(?: drive|:)(?![a-z])|\bdrive ([a-z])\b")]
    private static partial Regex DriveLetter();

    /// <summary>Words that carry no subject: what's left without them is what a search of the changes looks for.</summary>
    [GeneratedRegex(@"\b(when|what|which|did|do|does|was|were|is|are|has|have|had|i|my|me|the|a|an|on|in|of|to|it|this|that|pc|computer|rig|machine|get|got|last|first|any|anything|something|there|been|be|and|or|for|with|from|at|by|now|new|recently|time|please|tell|show|list|you|can|could|would|how|many|much|why|install\w*|uninstall\w*|updat\w*|chang\w*|remov\w*|add\w*)\b|[?!.,;:'""]")]
    private static partial Regex Filler();
}
