using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Tests.Core;

namespace Rigsight.Tests.App;

/// <summary>
/// The dates under a game's "How it runs": oldest first, the year on the first month and wherever it turns, and never
/// two touching, in English and in Spanish, down to a narrow chart (2026-11-11: on the 12 months, "Dec" left of "Oct"
/// with no year anywhere read as out of order).
/// </summary>
public sealed class RunsChartDateTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 19, 40, 0);
    private static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>As wide as the chart draws it (Segoe UI at 11), in the culture in use now.</summary>
    private static Func<string, double> Measure()
    {
        var known = new Dictionary<string, double>();
        return text => known.TryGetValue(text, out var w) ? w
            : known[text] = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Font, 11, Brushes.Black, 1.0).Width;
    }

    /// <summary>Narrow enough never to make a date give way, so only the year's rule decides the words.</summary>
    private static double Narrow(string text) => text.Length * 3.0;

    /// <summary>English, Spain's Spanish, and Spanish as the app shows it: Windows' region with the pack's month names.</summary>
    private static IEnumerable<(string Name, CultureInfo Culture)> Cultures()
    {
        var pack = LanguagePack.Read(File.ReadAllText(Path.Combine(Repo.Root, "languages", "es.json")));
        yield return ("en-US", CultureInfo.GetCultureInfo("en-US"));
        yield return ("es-ES", CultureInfo.GetCultureInfo("es-ES"));
        yield return ("es pack on en-US", LanguageStore.NamesFor(pack, CultureInfo.GetCultureInfo("en-US")));
    }

    private static T In<T>(CultureInfo culture, Func<T> work)
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return work();
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void The_first_month_and_each_one_where_the_year_turns_have_the_year()
    {
        In(CultureInfo.GetCultureInfo("en-US"), () =>
        {
            // Every 2nd month skips January: the year is on the first, and on the first of the next year.
            Assert.Equal(["Dec 2025", "Feb 2026", "Apr", "Jun", "Aug", "Oct"], RunsChart.DateTicks(Now.AddDays(-365), Now, 520, Narrow).Select(t => t.Text));
            Assert.Equal(["Dec 2025", "Jan 2026", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov"],
                RunsChart.DateTicks(Now.AddDays(-365), Now, 900, Narrow).Select(t => t.Text));
            // All in one year, past or this one: its first month says which.
            Assert.Equal(["Apr 2025", "May", "Jun", "Jul", "Aug", "Sep"], RunsChart.DateTicks(new DateTime(2025, 3, 10), new DateTime(2025, 9, 10), 900, Narrow).Select(t => t.Text));
            // Days keep their words: no year, the weekday when every day has room.
            Assert.Equal(["Thu 5 Nov", "Fri 6 Nov", "Sat 7 Nov", "Sun 8 Nov", "Mon 9 Nov", "Tue 10 Nov", "Wed 11 Nov"],
                RunsChart.DateTicks(Now.AddDays(-7), Now, 900, Narrow).Select(t => t.Text));
            return 0;
        });
    }

    [Fact]
    public void The_first_date_pushed_in_off_the_edge_onto_the_next_gives_way_and_the_next_takes_the_year()
    {
        In(CultureInfo.GetCultureInfo("en-US"), () =>
        {
            // From the 30th: the 1st of the next month sits at the very edge, pushed in onto the one after it.
            var from = new DateTime(2025, 11, 30, 19, 40, 0);
            var ticks = RunsChart.DateTicks(from, from.AddDays(365), 760, Measure());
            Assert.Equal("Jan 2026", ticks[0].Text);
            Assert.DoesNotContain(ticks, t => t.At == new DateTime(2025, 12, 1));
            return 0;
        });
    }

    [Fact]
    public void Dates_run_oldest_first_and_never_touch_in_English_or_Spanish_down_to_520_px()
    {
        double[] widths = [520, 540, 580, 620, 700, 730, 760, 820, 900, 1000, 1150, 1400];
        int[] ranges = [7, 30, 45, 62, 63, 90, 365, 730];
        foreach (var (name, culture) in Cultures())
            In(culture, () =>
            {
                var measure = Measure();
                for (var end = new DateTime(2026, 1, 1); end < new DateTime(2027, 1, 1); end = end.AddDays(3))
                    foreach (var time in new[] { new TimeSpan(0, 30, 0), new TimeSpan(12, 5, 0), new TimeSpan(19, 40, 0) })
                        foreach (int days in ranges)
                            foreach (double width in widths)
                            {
                                DateTime to = end + time, from = to.AddDays(-days);
                                var ticks = RunsChart.DateTicks(from, to, width, measure);
                                string where = $"{name}, {days} days to {to:yyyy-MM-dd HH:mm}, {width} px: {string.Join(" | ", ticks.Select(t => t.Text))}";
                                Assert.True(ticks.Count >= 2, where);
                                for (int i = 0; i < ticks.Count; i++)
                                {
                                    double x = RunsChart.DateX(ticks[i].At, from, to, width), w = measure(ticks[i].Text);
                                    Assert.True(x - w / 2 >= 0 && x + w / 2 <= width, $"outside the chart: {where}");
                                    if (i == 0) continue;
                                    Assert.True(ticks[i].At > ticks[i - 1].At, $"out of order: {where}");
                                    double before = RunsChart.DateX(ticks[i - 1].At, from, to, width) + measure(ticks[i - 1].Text) / 2;
                                    Assert.True(x - w / 2 >= before + 6 - 1e-9, $"touching: {where}");
                                }
                                if (days <= 62) continue;
                                // Months: a year on the first, and on each where the year turns, and only there.
                                for (int i = 0; i < ticks.Count; i++)
                                {
                                    bool hasYear = ticks[i].Text.Contains(ticks[i].At.Year.ToString(CultureInfo.InvariantCulture));
                                    Assert.True(hasYear == (i == 0 || ticks[i].At.Year != ticks[i - 1].At.Year), $"year: {where}");
                                }
                            }
                return 0;
            });
    }
}
