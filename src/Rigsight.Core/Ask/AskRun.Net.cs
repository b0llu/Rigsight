using System.Text.RegularExpressions;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    private AskAnswer Network()
    {
        var period = PeriodOr(() => Today);
        var a = new AskAnswer();
        a.Links.Add(new("Open Network", "network"));
        if (_db.FirstNetDay() is not long firstNet)
        {
            a.Lead = "No internet use has been recorded yet.";
            return a;
        }
        if (period.To <= TimeUtil.FromUnix(firstNet))
        {
            a.Lead = $"No internet use is recorded {period.Label}: those records start on {AskTime.DayText(TimeUtil.FromUnix(firstNet), Now)}.";
            return a;
        }

        long from = TimeUtil.ToUnix(period.From), to = TimeUtil.ToUnix(period.To);
        bool wholeDays = period.From.TimeOfDay == TimeSpan.Zero && period.To.TimeOfDay == TimeSpan.Zero;
        long down, up, background;
        List<NetAppUse> apps;
        if (wholeDays)
        {
            var days = _db.GetNetDays(from, to);
            (down, up, background) = (days.Sum(d => d.Down), days.Sum(d => d.Up), days.Sum(d => d.Background));
            apps = _db.GetNetAppTotals(from, to);
        }
        else
        {
            // Part of a day: from the minutes, and each app's hours that fall wholly inside it.
            var minutes = _db.GetNetMinutes(from, to);
            (down, up, background) = (minutes.Sum(m => m.Down), minutes.Sum(m => m.Up), minutes.Sum(m => m.BgDown + m.BgUp + m.AwayDown + m.AwayUp));
            apps = [.. _db.GetNetAppHours(TimeUtil.ToUnix(ReportBuilder.HourStart(period.From)), to).GroupBy(u => u.App).Select(g => new NetAppUse
            {
                App = g.Key, Down = g.Sum(u => u.Down), Up = g.Sum(u => u.Up), BgDown = g.Sum(u => u.BgDown), BgUp = g.Sum(u => u.BgUp),
                AwayDown = g.Sum(u => u.AwayDown), AwayUp = g.Sum(u => u.AwayUp), GameDown = g.Sum(u => u.GameDown), Lan = g.Sum(u => u.Lan),
            })];
        }
        apps = [.. apps.Where(u => u.Total > 0 && _apps.ContainsKey(u.App)).OrderByDescending(u => u.Total)];
        var drops = _db.GetNetDrops(from, to);
        var net = period.Range is ReportRange.Day or ReportRange.Week or ReportRange.Month or ReportRange.Year ? ReportFor(period).Net : null;
        string lower = _q.Text.ToLowerInvariant();

        // Did it drop?
        if (DropWords().IsMatch(lower))
        {
            if (drops.Count == 0 && down + up == 0)
            {
                a.Lead = $"No internet use was recorded {period.Label}, so I can't say whether it dropped.";
                return a;
            }
            if (drops.Count == 0)
            {
                a.Lead = $"**No drops {period.Label}.** The connection held the whole time the PC was on.";
            }
            else
            {
                long seconds = drops.Sum(d => d.Seconds);
                a.Lead = $"**Your internet dropped {(drops.Count == 1 ? "once" : $"{drops.Count} times")} {period.Label}**, for {Dur(seconds)} in all.";
                foreach (var d in drops.OrderByDescending(d => d.Seconds).Take(5).OrderBy(d => d.Start))
                    a.Points.Add(new($"**{AtIn(TimeUtil.FromUnix(d.Start), period).ToUpperFirst()}**: {Dur(d.Seconds)}, {(d.Kind == NetDropKind.Link ? "the cable or Wi-Fi itself went" : "past the PC (the router or the provider)")}",
                        AskTone.Warn));
                int link = drops.Count(d => d.Kind == NetDropKind.Link);
                a.Paragraphs.Add(link == drops.Count ? "Each time it was the PC's own link: the cable, the Wi-Fi signal, or the adapter."
                    : link == 0 ? "Each time the PC stayed linked to the router, so the fault was past it: the router or the provider."
                    : $"{link} of them were the PC's own link (cable or Wi-Fi); the rest were past the router.");
            }
            if (net?.Insights.FirstOrDefault(i => i.Key == "net-drops") is { } pattern) a.Points.Add(new(pattern.Text, ToneOf(pattern.Tone)));
            a.FollowUps.Add($"How much data did I use {period.Label}?");
            return a;
        }

        if (down + up == 0)
        {
            a.Lead = $"No internet use was recorded {period.Label}.";
            return a;
        }

        // One app's share.
        if (_q.AppId is long appId)
        {
            string app = NameOf(appId);
            var use = apps.FirstOrDefault(u => u.App == appId);
            if (use is null)
            {
                a.Lead = $"{app} didn't use the internet {period.Label}.";
            }
            else
            {
                a.Lead = $"**{app}** used **{Units.Data(use.Total)}** {period.Label}: {Units.Data(use.Down)} down, {Units.Data(use.Up)} up.";
                if (down + up > 0) a.Paragraphs.Add($"That is {((double)use.Total / (down + up) is var share && share < 0.01 ? "under 1%" : share.ToString("P0"))} of everything the PC moved in that time.");
                if (use.Background > use.Total / 2) a.Paragraphs.Add($"{Units.Data(use.Background)} of it went by while it wasn't in front.");
                if (use.GameDown > 100L << 20) a.Paragraphs.Add($"{Units.Data(use.GameDown)} came down while a game was in front.");
            }
            a.FollowUps.Add($"What used the most data {period.Label}?");
            return a;
        }

        // How fast?
        bool speed = SpeedWords().IsMatch(lower);
        if (speed)
        {
            var best = wholeDays ? _db.GetNetDays(from, to).Where(d => d.Best is not null).MaxBy(d => d.Best) : null;
            if (best is { Best: long fastest, BestTs: long at })
            {
                a.Lead = $"The fastest steady download {period.Label} was **{Units.Mbps(fastest)}** ({AtIn(TimeUtil.FromUnix(at), period)}).";
                a.Note = "That is the speed a real download held, not a speed test: the line itself may be faster than anything asked of it.";
            }
            else
            {
                a.Lead = $"Nothing downloaded steadily enough {period.Label} to say how fast the line is.";
                a.Note = "Speed is read from real downloads, not from a speed test.";
            }
            if (net?.Insights.FirstOrDefault(i => i.Key == "net-speed") is { } slower) a.Points.Add(new(slower.Text, ToneOf(slower.Tone)));
            if (drops.Count > 0) a.Points.Add(new($"The connection dropped {(drops.Count == 1 ? "once" : $"{drops.Count} times")} {period.Label}.", AskTone.Warn));
            if (apps.Count > 0 && SlowWords().IsMatch(lower))
                a.Points.Add(new($"Using it most {period.Label}: {Join([.. apps.Take(3).Select(u => $"{NameOf(u.App)} ({Units.Data(u.Total)})")])}."));
            a.FollowUps.Add($"Did my internet drop {period.Label}?");
            a.FollowUps.Add($"What used the most data {period.Label}?");
            return a;
        }

        // The totals, and who used them.
        a.Lead = $"You used **{Units.Data(down + up)}** {period.Label}: {Units.Data(down)} down, {Units.Data(up)} up.";
        if (apps.Count > 0)
        {
            a.PointsTitle = "What used the most";
            foreach (var u in apps.Take(5)) a.Points.Add(new($"**{NameOf(u.App)}**: {Units.Data(u.Total)}"));
        }
        if (background > (down + up) * 0.4 && background > 200L << 20)
            a.Paragraphs.Add($"{Units.Data(background)} of it moved in the background, by apps that weren't in front or while you were away.");
        var big = _db.GetNetTransfers(from, to, 3).Where(x => _apps.ContainsKey(x.App) && x.Bytes >= 500L << 20 && x.Bytes <= down).ToList();
        if (big.Count > 0)
            a.Paragraphs.Add($"The biggest single download: {Units.Data(big[0].Bytes)} by {NameOf(big[0].App)}, {AtIn(TimeUtil.FromUnix(big[0].Start), period)}.");
        if (net is not null)
            foreach (var i in net.Insights.Where(i => i.Key is "net-background" or "net-upload" or "net-sharing" or "net-record" or "net-drops").Take(2))
                a.Points.Add(new(i.Text, ToneOf(i.Tone)));
        a.Facts.Add(new("Down", Units.Data(down)));
        a.Facts.Add(new("Up", Units.Data(up)));
        if (drops.Count > 0) a.Facts.Add(new("Drops", drops.Count.ToString(), AskTone.Warn));
        a.FollowUps.Add($"Did my internet drop {period.Label}?");
        if (apps.Count > 0) a.FollowUps.Add($"How much data did {NameOf(apps[0].App)} use this month?");
        return a;
    }

    private AskAnswer Storage()
    {
        var a = new AskAnswer();
        a.Links.Add(new("Open Storage", "storage"));
        var drives = DrivesNow();
        if (_q.Drive is { } letter) drives = [.. drives.Where(d => d.Drive.StartsWith(letter, StringComparison.OrdinalIgnoreCase))];
        if (drives.Count == 0)
        {
            a.Lead = _q.Drive is not null ? $"There's no drive {_q.Drive}: on record." : "Drive space hasn't been recorded yet. It's read once a day.";
            return a;
        }
        static double Free(DriveDay d) => d.TotalGb - d.UsedGb;
        // Nine tenths full, or under 15 GB left on a drive big enough for that to be little (not a 500 MB recovery partition).
        static bool Tight(DriveDay d) => d.UsedGb / d.TotalGb >= 0.9 || (Free(d) < 15 && d.TotalGb >= 100);
        static string Name(DriveDay d) => d.Drive.TrimEnd('\\');

        var tight = drives.Where(Tight).ToList();
        if (drives.Count == 1)
        {
            var d = drives[0];
            a.Lead = $"**{Name(d)}** is **{d.UsedGb / d.TotalGb:P0} full**: {Free(d):N0} GB free of {d.TotalGb:N0}.";
            if (Tight(d)) a.Paragraphs.Add("That is tight. Windows and games need spare room for updates, and slow down without it.");
        }
        else
        {
            a.Lead = tight.Count == 0 ? $"**Plenty of room.** The fullest drive, {Name(drives.MinBy(d => Free(d) / d.TotalGb)!)}, still has {Free(drives.MinBy(d => Free(d) / d.TotalGb)!):N0} GB free."
                : $"**{Join([.. tight.Select(Name)])} {(tight.Count == 1 ? "is" : "are")} nearly full.**";
            foreach (var d in drives)
                a.Facts.Add(new(Name(d), $"{Free(d):N0} GB free", Tight(d) ? AskTone.Warn : AskTone.Neutral));
        }

        // How it got there: the days it jumped or dropped, and what was installed those days.
        var period = _q.Period ?? AskTime.LastDays(30, Now);
        var history = _db.GetDriveDays(TimeUtil.ToUnix(period.From.AddDays(-1))).Where(d => drives.Any(x => string.Equals(x.Drive, d.Drive, StringComparison.OrdinalIgnoreCase))).ToList();
        var jumps = StorageChanges.From(history).Where(c => c.Time >= period.From && c.Time < period.To).OrderByDescending(c => c.Time).ToList();
        var installs = _db.HasChanges ? _db.GetChanges(TimeUtil.ToUnix(period.From), TimeUtil.ToUnix(period.To)).Where(c => c.Kind == ChangeKind.AppInstalled).ToList() : [];
        foreach (var d in drives)
        {
            var then = history.Where(h => string.Equals(h.Drive, d.Drive, StringComparison.OrdinalIgnoreCase) && TimeUtil.FromUnix(h.Day) >= period.From).MinBy(h => h.Day);
            if (then is null || then.Day == d.Day || Math.Abs(d.UsedGb - then.UsedGb) < 5 || Math.Abs(then.TotalGb - d.TotalGb) > d.TotalGb * 0.01) continue;
            double delta = d.UsedGb - then.UsedGb;
            a.Paragraphs.Add($"{Name(d)} has {(delta > 0 ? $"{delta:N0} GB more" : $"{-delta:N0} GB less")} in use than on {AskTime.DayText(TimeUtil.FromUnix(then.Day), Now)}.");
        }
        if (jumps.Count > 0)
        {
            a.PointsTitle = $"The big moves {period.Label}";
            foreach (var j in jumps.Take(5))
            {
                var sameDay = installs.Where(i => i.Time.Date == j.Time.Date || i.Time.Date == j.Time.Date.AddDays(-1)).Select(i => i.Title.Replace(" installed", "")).Take(2).ToList();
                a.Points.Add(new($"**{AskTime.DayText(j.Time, Now)}**: {j.Title}{(sameDay.Count > 0 ? $" (installed around then: {Join(sameDay)})" : "")}"));
            }
        }
        if (_q.Why && jumps.Count == 0 && a.Paragraphs.Count == 0)
            a.Paragraphs.Add($"Nothing grew or shrank by {StorageChanges.MinGb:0} GB or more in a day {period.Label}, so there is no one moment to point at.");
        if (WhatTakesWords().IsMatch(_q.Text.ToLowerInvariant()))
            a.Paragraphs.Add("To see which folders take the space, run a scan on the Storage page: it maps a whole drive.");
        a.Note = "Free space is read once a day.";
        a.FollowUps.Add("What was installed in the last 30 days?");
        return a;
    }

    [GeneratedRegex(@"\b(drop\w*|disconnect\w*|cut(ting|s)? out|went (down|out|off)|go(es|ing)? (down|out)|outage\w*|offline|lost (the )?connection|unstable|flak\w*|kept? (dying|going))\b")]
    private static partial Regex DropWords();

    [GeneratedRegex(@"\b(fast|faster|speed\w*|mbps|slow\w*|bandwidth speed)\b")]
    private static partial Regex SpeedWords();

    [GeneratedRegex(@"\bslow\w*\b")]
    private static partial Regex SlowWords();

    [GeneratedRegex(@"\b(what('s| is| was)? (is )?(taking|using|eating|ate|filling|filled|took)|which (folders?|files?|apps?|games?))\b")]
    private static partial Regex WhatTakesWords();
}
