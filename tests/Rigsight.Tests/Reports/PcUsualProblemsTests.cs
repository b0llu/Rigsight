using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.ViewModels;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The PC's problems are set against a usual of a week or more only. A month's report said shut-offs that look like the
/// power were "usually none" from the 3 days recorded before it, while the week after said "about 4 a week" (real PC,
/// 26 Oct 2026). With less than a week there are no problem rows, and the pages read as they do on a new PC.
/// </summary>
[Collection("UI")]
public sealed class PcUsualProblemsTests
{
    private static readonly DateTime From = new(2026, 9, 26);

    /// <summary>The month from <see cref="From"/>, 10 hours of use a day, with <paramref name="usualDays"/> days of use before it and no problems.</summary>
    private static PcUsual Usual(int usualDays, DateTime? from = null, int nowDays = 30)
    {
        var start = from ?? From;
        var use = Enumerable.Range(-usualDays, usualDays + nowDays).Select(d => new SystemDay { Day = U(start.AddDays(d)), Minutes = 600, ActiveSec = 10 * 3600 }).ToList();
        var to = start.AddDays(nowDays);
        return PcUsual.Of(start, to, start.AddDays(-60), to, [], use, [], new Dictionary<long, CrashFinding>(), [], [], [], _ => "a game");
    }

    private static Report Month(PcUsual usual) => new() { Range = ReportRange.Custom, From = From, To = From.AddDays(30), Usual = usual };

    [Theory]
    [InlineData(3, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void Problems_are_set_against_a_usual_of_a_week_or_more_only(int usualDays, bool rows)
    {
        // 3 days are 30 hours of use, past the bar of 10: the days, not the hours, keep the rows out.
        var usual = Usual(usualDays);
        Assert.Equal(rows, usual.Problems is not null);
        var cardRows = PcUsual.CardRows(Month(usual));
        Assert.Equal(rows, cardRows.Any(r => r.Key == "problems"));
        if (rows) Assert.Equal("None", Assert.Single(cardRows, r => r.Key == "problems").Usual);
    }

    [Fact]
    public void With_too_short_a_usual_the_pages_read_as_on_a_new_pc()
    {
        using var _ = Culture();
        // Reports: the card holds only what moved, so with no other rows there's no card at all (no empty heading).
        var month = Month(Usual(3));
        Assert.Empty(PcUsual.CardRows(month));
        Assert.Empty(ReportsViewModel.UsualOf(month));

        // Home: the week's problems are said as they were, with nothing about a usual.
        var today = DateTime.Today;
        var day = new Report { Range = ReportRange.Day, From = today, To = today.AddDays(1), Usual = Usual(3, today.AddDays(-6), 7) };
        Assert.Null(day.Usual!.Problems);
        var lines = HomeViewModel.HealthLines(day, [CrashKind.UnexpectedShutdown, CrashKind.UnexpectedShutdown], 0, [], null, null);
        var line = Assert.Single(lines, l => l.Text.StartsWith("2 sudden shut-offs", StringComparison.Ordinal));
        Assert.DoesNotContain("usual", line.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("No PC problems", StringComparison.Ordinal));
    }
}
