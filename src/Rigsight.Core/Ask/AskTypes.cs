using Rigsight.Core.Reports;

namespace Rigsight.Core.Ask;

/// <summary>What a question is about. Each has its own answer, built from what's recorded.</summary>
public enum AskIntent
{
    /// <summary>Not understood, or not something the PC's history can answer.</summary>
    None,
    Crashes, Changes, Temps, Usage, TopApps, Slow, Memory, Network, Storage, Fans, Health, Specs,
    /// <summary>One reading over a time: its average, its highest or its lowest ("average CPU load while Chrome was open").</summary>
    Metric,
    Help, Hello, Thanks, Who, Other,
}

/// <summary>A reading a question can ask the average, highest or lowest of.</summary>
public enum AskMetric { None, CpuLoad, GpuLoad, CpuPower, GpuPower, Power, CpuClock, GpuClock, CpuTemp, GpuTemp, HotSpot, Ram, CpuVolt, GpuVolt }

/// <summary>Which figure of a reading is asked for (Any: all three, the average first).</summary>
public enum AskAggregate { Any, Average, Highest, Lowest }

/// <summary>A part of the PC a question names.</summary>
public enum AskPart { None, Cpu, Gpu, Memory, Fans, Drive, Network }

/// <summary>
/// The stretch of time a question is about. <see cref="Range"/> is the calendar unit when it is one whole day, week,
/// month or year (so the report for it is the same one its page shows), Custom otherwise. <see cref="Label"/> reads
/// after "…" in a sentence ("yesterday", "last week", "on Fri 2 Oct", "in the last 7 days").
/// </summary>
public sealed record AskPeriod(DateTime From, DateTime To, ReportRange Range, string Label)
{
    public bool IsDay => Range == ReportRange.Day;

    /// <summary>False for a date that doesn't exist ("31 Feb"): <see cref="Label"/> is then what was typed.</summary>
    public bool IsReal => To > From;
    public bool IsToday => IsDay && From.Date == DateTime.Today;

    /// <summary>The label to start a sentence with ("Yesterday", "Last week").</summary>
    public string Title => Label.Length == 0 ? "" : char.ToUpperInvariant(Label[0]) + Label[1..];

    /// <summary>Whole days it covers.</summary>
    public int Days => Math.Max(1, (int)Math.Ceiling((To - From).TotalDays));
}

/// <summary>A question as it was read: what it asks, about when, and about which app or part.</summary>
public sealed record AskQuery(string Text)
{
    public AskIntent Intent { get; init; }

    /// <summary>How sure the reading is, 0 to 1 (the closeness of the nearest known way of asking).</summary>
    public double Score { get; init; }

    /// <summary>The next nearest reading, offered when the first isn't sure enough.</summary>
    public AskIntent Second { get; init; }

    /// <summary>Null when the question names no time: each answer then has its own usual period.</summary>
    public AskPeriod? Period { get; init; }

    /// <summary>The time to set <see cref="Period"/> against, when the question compares two ("this week than last week").</summary>
    public AskPeriod? Against { get; init; }

    public long? AppId { get; init; }
    public string? App { get; init; }
    public AskPart Part { get; init; }

    /// <summary>The reading asked about, for <see cref="AskIntent.Metric"/>, and which figure of it.</summary>
    public AskMetric Metric { get; init; }
    public AskAggregate Aggregate { get; init; }

    /// <summary>
    /// The kind of problem the conversation is on (a <see cref="Stability.CrashKind"/> by name) and, for an app's crash,
    /// its exe: what "that" means in "when did that start happening?".
    /// </summary>
    public string? Problem { get; init; }
    public string? ProblemApp { get; init; }

    /// <summary>It asks for the history of that problem: when it began, how often it happens.</summary>
    public bool History { get; init; }

    /// <summary>"C" when a drive letter was named.</summary>
    public string? Drive { get; init; }

    /// <summary>The question asks for a cause ("why", "what caused"), not a list.</summary>
    public bool Why { get; init; }

    /// <summary>It asks when something last happened ("when did I last…", "the last time").</summary>
    public bool Last { get; init; }

    /// <summary>
    /// What looks like an app's name in the question but matches nothing on this PC ("fortnite" where it was never
    /// run): the answer says so, and doesn't answer for the whole PC as if no app had been named.
    /// </summary>
    public string? UnknownApp { get; init; }

    /// <summary>A second app, when the question sets two side by side ("do I play more Rematch or Dota?").</summary>
    public long? OtherAppId { get; init; }
    public string? OtherApp { get; init; }

    /// <summary>The time the conversation stays on, when this question looked at another for its answer (the days before a crash).</summary>
    public AskPeriod? Anchor { get; init; }

    /// <summary>Its time was not named in it: it is the time the conversation was on.</summary>
    public bool CarriedTime { get; init; }

    /// <summary>What the question would be about if it didn't lean on the one before: offered as "no, I meant…".</summary>
    public AskIntent Alternative { get; init; }

    /// <summary>A telling word of its topic is in the question ("hot" for temperatures): it stands on its own feet.</summary>
    public bool Backed { get; init; }

    /// <summary>Nothing but a time was typed ("yesterday?").</summary>
    public bool BarePeriod { get; init; }

    /// <summary>It asks which app works a part hardest ("what uses the most CPU").</summary>
    public bool TopBy { get; init; }

    /// <summary>It asks how things stand this minute ("right now", "current"), not over a day.</summary>
    public bool Now { get; init; }

    /// <summary>It is an insult, not a question: answered kindly, and not held against anyone.</summary>
    public bool Rude { get; init; }

    /// <summary>It asks about games as a whole ("how much did I game").</summary>
    public bool Games { get; init; }

    /// <summary>It asks how to do something: advice, which the PC's history can't give.</summary>
    public bool HowTo { get; init; }

    /// <summary>It leans on the question before ("and the day before?", "what about the CPU?").</summary>
    public bool FollowsUp { get; init; }

    /// <summary>The wording without its time and with its app as "this app": what gets remembered when it is taught.</summary>
    public string Generic { get; init; } = "";

    /// <summary>The words left once the time, the app and the part were taken out (what a change search looks for).</summary>
    public string Rest { get; init; } = "";
}

/// <summary>What the conversation was last about, so a short follow-up can lean on it.</summary>
public sealed record AskContext(AskIntent Intent, AskPeriod? Period, long? AppId, string? App, AskPart Part, bool Why = false,
    string? Problem = null, string? ProblemApp = null, AskMetric Metric = AskMetric.None);

public enum AskTone { Neutral, Good, Warn, Hot }

/// <summary>One thing that was checked and what it showed ("Heat: GPU at 68°, under your 83° limit").</summary>
/// <param name="Group">What it says about the cause, where an answer sorts its evidence ("Points to", "Ruled out"…); "" otherwise.</param>
public sealed record AskPoint(string Text, AskTone Tone = AskTone.Neutral, string Group = "");

/// <summary>One step on the way to something (a crash): when, and what the records show then.</summary>
public sealed record AskStep(string When, string Text, AskTone Tone = AskTone.Neutral);

/// <summary>A number worth a tile of its own.</summary>
public sealed record AskFact(string Label, string Value, AskTone Tone = AskTone.Neutral);

/// <summary>An app offered for a name that wasn't recognised.</summary>
public sealed record AskAppChoice(string Name, string Exe);

/// <summary>A page that shows more, opened on a day when one matters.</summary>
public sealed record AskLink(string Text, string Page, DateTime? Day = null);

/// <summary>
/// An answer: the verdict first (<see cref="Lead"/>), then what backs it. Text may mark words as **strong**. Every
/// number in it was read from the PC's history by code; none of the wording is generated.
/// </summary>
public sealed class AskAnswer
{
    public string Lead { get; set; } = "";
    public List<string> Paragraphs { get; } = [];
    public List<AskFact> Facts { get; } = [];
    public string? PointsTitle { get; set; }
    public List<AskPoint> Points { get; } = [];

    /// <summary>What led up to it, in order, as far as the records show (once a minute).</summary>
    public List<AskStep> Trail { get; } = [];
    public string? TrailTitle { get; set; }

    /// <summary>What to do about it, when there's something to do.</summary>
    public string? Advice { get; set; }

    /// <summary>What the answer can't say, and why (history is per minute, a reading wasn't kept…).</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Said above the answer when its time was carried over from earlier in the conversation ("Still about Tue 29 Sep"),
    /// so a number about another day is never taken for today's.
    /// </summary>
    public string? Carried { get; set; }

    /// <summary>The same question about today, offered beside <see cref="Carried"/>.</summary>
    public string? CarriedInstead { get; set; }

    /// <summary>How the question was read ("Crashes · yesterday"), shown under the answer.</summary>
    public string Read { get; set; } = "";

    public List<AskLink> Links { get; } = [];

    /// <summary>Questions worth asking next, ready to click.</summary>
    public List<string> FollowUps { get; } = [];

    /// <summary>
    /// Apps to pick from when a name in the question matched none ("did you mean…"): picking one answers the question
    /// for it, and the name is remembered as that app's from then on.
    /// </summary>
    public List<AskAppChoice> AppChoices { get; } = [];

    /// <summary>False for "I didn't get that" and for small talk: nothing to lean a follow-up on.</summary>
    public bool Understood { get; set; } = true;

    public AskContext? Context { get; set; }

    /// <summary>The question as it was read (what a correction teaches against).</summary>
    public AskQuery? Query { get; set; }

    /// <summary>All of it as plain text (for copying, and for tests that read an answer whole).</summary>
    public string PlainText()
    {
        var lines = new List<string> { Lead };
        lines.AddRange(Paragraphs);
        if (Facts.Count > 0) lines.Add(string.Join(" · ", Facts.Select(f => $"{f.Label}: {f.Value}")));
        if (Points.Count > 0 && PointsTitle is not null) lines.Add(PointsTitle);
        foreach (var group in Points.GroupBy(p => p.Group))
        {
            if (group.Key.Length > 0) lines.Add(group.Key);
            lines.AddRange(group.Select(p => "• " + p.Text));
        }
        if (Trail.Count > 0 && TrailTitle is not null) lines.Add(TrailTitle);
        lines.AddRange(Trail.Select(s => $"{s.When}  {s.Text}"));
        if (Advice is not null) lines.Add(Advice);
        if (Note is not null) lines.Add(Note);
        return string.Join(Environment.NewLine, lines.Where(l => l.Length > 0)).Replace("**", "");
    }
}

/// <summary>
/// Turns a sentence into a point in space where sentences that mean alike sit close together (a small language model
/// on this PC). Rows have length 1, so the closeness of two is their dot product.
/// </summary>
public interface IAskEmbedder
{
    float[][] Embed(IReadOnlyList<string> texts);

    /// <summary>Whether this is a word the model knows whole (so a real word, not a slip of the keyboard).</summary>
    bool IsWord(string word);
}
