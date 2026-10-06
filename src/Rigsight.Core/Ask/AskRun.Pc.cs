using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    /// <summary>How the PC did over a period: what needs a look first, then the period in a few lines.</summary>
    private AskAnswer Health()
    {
        var period = PeriodOr(() => Today);
        var r = ReportFor(period);
        if (NothingRecorded(period, r) is { } none) return none;
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports", period.IsDay ? period.From : null));

        var concerns = r.Insights.Where(i => i.Tone is InsightTone.Warn or InsightTone.Hot).ToList();
        var crashes = r.Crashes.Where(c => Severity(c) > 0).ToList();
        bool temps = r.GpuTempPeak is not null || r.CpuTempPeak is not null;
        if (concerns.Count == 0 && crashes.Count == 0)
            a.Lead = $"**All good {period.Label}.** No crashes{(temps ? ", temperatures within your limits" : "")}, and nothing out of the ordinary.";
        else
        {
            int n = Math.Max(concerns.Count, 1);
            a.Lead = $"**{Count(n, "thing is", "things are")} worth a look {period.Label}.**";
        }
        foreach (var i in concerns.Take(5)) a.Points.Add(new(i.Text, ToneOf(i.Tone)));
        if (crashes.Count > 0 && !concerns.Any(i => i.Key == "crash"))
            a.Points.Add(new($"{Tally(crashes).ToUpperFirst()}.", crashes.Any(IsPcLevel) ? AskTone.Hot : AskTone.Warn));

        // The period in a few lines: the report's own plain observations.
        foreach (var i in r.Insights.Where(i => i.Tone is InsightTone.Neutral or InsightTone.Good).Take(concerns.Count > 0 ? 2 : 4))
            a.Points.Add(new(i.Text, ToneOf(i.Tone)));

        a.Facts.Add(new("In use", Dur(r.ActiveSec)));
        if (r.GpuTempPeak is { } gpu) a.Facts.Add(new("GPU highest", Temp(gpu.Value), r.GpuOverLimitMin > 0 ? AskTone.Hot : AskTone.Neutral));
        if (r.CpuTempPeak is { } cpu) a.Facts.Add(new("CPU highest", Temp(cpu.Value), r.CpuOverLimitMin > 0 ? AskTone.Hot : AskTone.Neutral));
        a.Facts.Add(new("Crashes", crashes.Count.ToString(), crashes.Count > 0 ? AskTone.Warn : AskTone.Good));

        if (_db.HasChanges)
        {
            var changed = _db.GetChanges(TimeUtil.ToUnix(period.From), TimeUtil.ToUnix(period.To)).Where(c => c.IsSystemLevel).OrderByDescending(Weight).Select(Thing).Distinct().Take(3).ToList();
            if (changed.Count > 0) a.Paragraphs.Add($"Changed {period.Label}: {Join(changed)}.");
        }

        if (crashes.Count > 0) a.FollowUps.Add($"Why did {(crashes.Any(IsPcLevel) ? "my PC" : CrashName(crashes[0]))} crash {period.Label}?");
        a.FollowUps.Add($"What did I use most {period.Label}?");
        a.FollowUps.Add(IsToday(period) ? "How was my PC yesterday?" : "How is my PC doing today?");
        return a;
    }

    /// <summary>What's in the PC, from the inventory the agent keeps; one part when one is asked for.</summary>
    private AskAnswer Specs()
    {
        var a = new AskAnswer();
        a.Links.Add(new("Open Timeline", "timeline"));
        var items = (_db.HasChanges ? _db.GetInventory() : []).ToLookup(i => i.Kind);
        if (items.Count == 0)
        {
            a.Lead = "The PC's parts haven't been read yet. It takes a minute after the first start; ask again shortly.";
            return a;
        }
        var changes = _db.GetChanges(0, long.MaxValue / 2);
        string Since(Func<SystemChange, bool> match) => changes.LastOrDefault(match) is { } c ? $", since {AskTime.DayText(c.Time, Now)}" : "";
        string? Value(string kind) => items[kind].FirstOrDefault()?.Value is { Length: > 0 } v ? v : null;

        string? cpu = Value(Inventory.Cpu), ram = Value(Inventory.Ram), board = Value(Inventory.Board), bios = Value(Inventory.Bios);
        var cards = items[Inventory.Gpu].Select(i => i.Name).ToList();
        var driver = items[Inventory.GpuDriver].FirstOrDefault();
        var windows = items[Inventory.Windows].FirstOrDefault();
        var disks = items[Inventory.Disk].Select(i => i.Name).ToList();
        int apps = items[Inventory.App].Count(), startup = items[Inventory.Startup].Count(i => i.Value == Inventory.On);
        string lower = _q.Text.ToLowerInvariant();

        // One thing asked for.
        string? one =
            lower.Contains("driver") && driver is not null ? $"The {(driver.Name.StartsWith("Graphics", StringComparison.Ordinal) ? "graphics driver" : driver.Name)} is version **{driver.Value}**{Since(c => c.IsGraphicsDriver)}."
            : BiosWords().IsMatch(lower) && bios is not null ? $"The BIOS is version **{bios}**{Since(c => c.Kind == ChangeKind.Firmware)}."
            : BoardWords().IsMatch(lower) && board is not null ? $"The motherboard is a **{board}**."
            : WindowsWords().IsMatch(lower) && windows is not null ? $"This is **{windows.Name} {windows.Value}**{Since(c => c.Kind == ChangeKind.Windows)}."
            : AppCountWords().IsMatch(lower) && apps > 0 ? $"**{apps:N0} apps** are installed, and {startup:N0} start with Windows."
            : _q.Part == AskPart.Gpu && cards.Count > 0 ? $"The graphics card is {(cards.Count == 1 ? "a " : "")}**{Join(cards)}**{(driver is not null ? $", on driver {driver.Value}" : "")}."
            : _q.Part == AskPart.Cpu && cpu is not null ? $"The processor is a **{cpu.Trim()}**."
            : _q.Part == AskPart.Memory && ram is not null ? $"There is **{ram}** of memory installed."
            : _q.Part == AskPart.Drive && disks.Count > 0 ? $"{Count(disks.Count, "drive", "drives")} inside: **{Join(disks)}**."
            : null;
        if (one is not null)
        {
            a.Lead = one;
            a.FollowUps.Add("What's in this PC?");
            if (lower.Contains("driver")) a.FollowUps.Add("When did my graphics driver last change?");
            if (_q.Part == AskPart.Memory) a.FollowUps.Add("Do I need more RAM?");
            if (_q.Part == AskPart.Drive) a.FollowUps.Add("How full are my drives?");
            return a;
        }

        var main = new[] { cpu?.Trim(), cards.Count > 0 ? Join(cards) : null, ram is not null ? $"{ram} of memory" : null }.OfType<string>().ToList();
        a.Lead = main.Count > 0 ? $"This PC: **{Join(main)}**." : "Here's what's on record for this PC.";
        if (board is not null) a.Facts.Add(new("Motherboard", board));
        if (bios is not null) a.Facts.Add(new("BIOS", bios));
        if (windows is not null) a.Facts.Add(new("Windows", $"{windows.Name.Replace("Windows ", "")} {windows.Value}"));
        if (driver is not null) a.Facts.Add(new("Graphics driver", driver.Value));
        if (apps > 0) a.Facts.Add(new("Apps installed", apps.ToString("N0")));
        if (startup > 0) a.Facts.Add(new("Start with Windows", startup.ToString("N0")));
        if (disks.Count > 0) a.Paragraphs.Add($"{(disks.Count == 1 ? "Drive" : "Drives")}: {Join(disks)}.");
        a.FollowUps.Add("When did my graphics driver last change?");
        a.FollowUps.Add("How full are my drives?");
        a.FollowUps.Add("What changed on my PC this month?");
        return a;
    }

    [GeneratedRegex(@"\b(motherboard|mainboard|mobo|board)\b")]
    private static partial Regex BoardWords();

    [GeneratedRegex(@"\b(windows|os|operating system)\b")]
    private static partial Regex WindowsWords();

    [GeneratedRegex(@"\bhow many (apps|programs|games)\b|\bapps (are )?installed\b|\bstart(s)? with windows\b")]
    private static partial Regex AppCountWords();
}
