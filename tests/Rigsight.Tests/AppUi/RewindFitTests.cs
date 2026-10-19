using System.Windows;
using System.Windows.Media;
using Rigsight.Core.Rewind;
using Rigsight.Rewind;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Every card fits its picture: what a card says never needs more room than the card has, for a gaming PC, a PC that
/// never plays, a little play, a quiet PC, and three months and a year, in each shape. (The user, 2026-10-19: "most
/// of the UI you are making is going over the available card space".)
/// </summary>
[Collection("UI")]
public sealed class RewindFitTests
{
    private static RewindFacts Month()
    {
        var f = new RewindFacts
        {
            Id = "2026-10-month", Kind = RewindKind.Month, From = new(2026, 10, 1), To = new(2026, 11, 1), Made = new(2026, 11, 1, 9, 0, 0),
            Time = true, Games = true, Internet = true, Heat = true, Changes = true, Problems = true,
            OnSec = 312 * 3600, ActiveSec = 280 * 3600, DaysOn = 31, Days = 31, BusiestDay = new(2026, 10, 24), BusiestDaySec = 17 * 3600,
            DayHours = [.. Enumerable.Range(0, 31).Select(d => 6 + d % 9 * 1.2)],
            TopApps =
            [
                new("Steam Client WebHelper Service Host", "steamwebhelper.exe", null, 90 * 3600), new("Windows Terminal Host", "wt.exe", null, 70 * 3600),
                new("Microsoft Edge WebView2 Runtime", "msedgewebview2.exe", null, 40 * 3600), new("Discord", "discord.exe", null, 20 * 3600), new("Google Chrome", "chrome.exe", null, 9 * 3600),
            ],
            UseHours = [.. Enumerable.Range(0, 24).Select(h => h is > 8 and < 23 ? 10.0 + h % 5 : 0.5)],
            GamingSec = 140 * 3600, GameCount = 23, Plays = 210,
            TopGames =
            [
                new("Warhammer 40,000: Space Marine 2 – Ultra Edition", "sm2.exe", null, 60 * 3600, 70), new("Counter-Strike 2", "cs2.exe", null, 30 * 3600, 50),
                new("Baldur's Gate 3", "bg3.exe", null, 20 * 3600, 20), new("Slay the Spire 2", "sts2.exe", null, 15 * 3600, 30), new("No Man's Sky", "nms.exe", null, 9 * 3600, 8),
            ],
            LongestGame = new("Warhammer 40,000: Space Marine 2 – Ultra Edition", "sm2.exe", null, 7.5 * 3600), LongestStart = new(2026, 10, 24, 14, 5, 0), LongestEnd = new(2026, 10, 24, 22, 40, 0),
            PlayHours = [.. Enumerable.Range(0, 24).Select(h => h is >= 18 ? 12.0 : 1)], NightShare = 0.4,
            HotSpot = new(96, new(2026, 10, 24, 20, 0, 0), "Warhammer 40,000: Space Marine 2 – Ultra Edition", true),
            GpuHot = new(84, new(2026, 10, 24, 20, 0, 0), "Counter-Strike 2", true), CpuHot = new(91, new(2026, 10, 12, 20, 0, 0), "Microsoft Edge WebView2 Runtime", false),
            NewDriver = "621.10.25.1234", NewDriverAt = new(2026, 10, 30), Installed = 120, Updated = 340, Removed = 80,
            Down = 3L << 40, Up = 200L << 30,
            TopInternet = [new("Steam Client WebHelper Service Host", "s.exe", null, 2L << 40), new("Microsoft Edge WebView2 Runtime", "e.exe", null, 900L << 30), new("Discord", "d.exe", null, 100L << 30)],
            AppCrashes = 412, ShutOffs = 133,
            CrashesByDay = [.. Enumerable.Range(0, 31).Select(d => d % 7 * 4)], ShutOffsByDay = [.. Enumerable.Range(0, 31).Select(d => d % 3)],
        };
        return f;
    }

    private static RewindFacts Longer(RewindKind kind, int days)
    {
        var f = Month();
        f.Kind = kind; f.Id = $"long-{kind}"; f.From = new(2026, 1, 1); f.To = f.From.AddDays(days); f.Days = days; f.DaysOn = days - 9;
        f.DayHours = [.. Enumerable.Range(0, days).Select(d => 6 + d % 9 * 1.2)];
        f.CrashesByDay = [.. Enumerable.Range(0, days).Select(d => d % 7)]; f.ShutOffsByDay = [.. Enumerable.Range(0, days).Select(d => d % 5 == 0 ? 1 : 0)];
        f.OnSec *= days / 31.0; f.GamingSec *= days / 31.0;
        return f;
    }

    public static TheoryData<string> Cases => ["month", "no-games", "two-games", "quiet", "quarter", "year", "month in Spanish", "no-games in Spanish"];

    private static RewindFacts Case(string name) => name.Replace(" in Spanish", "") switch
    {
        "month" => Month(),
        "quarter" => Longer(RewindKind.Quarter, 92),
        "year" => Longer(RewindKind.Year, 365),
        var sample => RewindSamples.Of(Month()).Single(s => s.Id.EndsWith(sample)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_card_fits_its_picture(string name)
    {
        var f = Case(name);
        var problems = new List<string>();
        // Spanish sentences run longer than English ones: they have to fit as well.
        var pack = name.EndsWith("in Spanish") ? Rigsight.Core.LanguagePack.Load(Path.Combine(Rigsight.Tests.Core.Repo.Root, "languages", "es.json")) : null;
        Ui.Run(() =>
        {
            using var _ = Rigsight.Core.Lang.UseHere(pack);
            var cards = RewindCards.Of(f);
            foreach (var shape in new[] { "story", "post", "wide" })
                for (int i = 0; i < cards.Count; i++)
                {
                    var (w, h) = RewindPainter.Sizes[shape];
                    var card = RewindPainter.Card(cards[i], f, i, cards.Count, shape, new RewindLook("mono"), w, h);
                    card.Measure(new Size(w, h));
                    card.Arrange(new Rect(0, 0, w, h));
                    card.UpdateLayout();
                    var body = Find(card);
                    Assert.NotNull(body);
                    // What the card says, against the room it was given (a part taller than its room is cut off).
                    body.Measure(new Size(body.ActualWidth, double.PositiveInfinity));
                    double needs = body.DesiredSize.Height - body.Margin.Top - body.Margin.Bottom;
                    if (needs > body.ActualHeight + 1)
                        problems.Add($"{name} {shape} card {i + 1} ({cards[i].Id}): needs {needs:0} of {body.ActualHeight:0}");
                }
        });
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static FrameworkElement? Find(DependencyObject root)
    {
        if (root is FrameworkElement { Name: RewindPainter.BodyName } e) return e;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
