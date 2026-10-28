using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.PlansTests;

namespace Rigsight.Tests.App;

/// <summary>
/// Yearly plans (2026-10-28): each paid plan's yearly option read from the relay, the Plans screen's Monthly / Yearly
/// switch (Yearly when it opens) showing the price a month with the real charge and the saving, and a yearly plan said as
/// one in the plan box.
/// </summary>
[Collection("UI")]
public sealed class YearlyTests
{
    private const string EssentialsYear = ""","yearly":{"price_cents":4999,"price_label":"$49.99","url":"https://rigsight.lemonsqueezy.com/checkout/buy/ey"}""";
    private const string CompleteYear = ""","yearly":{"price_cents":9999,"price_label":"$99.99","url":"https://rigsight.lemonsqueezy.com/checkout/buy/cy"}""";

    /// <summary>The status at $4.99 / $9.99 with each plan's yearly option; <paramref name="billing"/> on the plan when given.</summary>
    private static string WithYearly(string status, string? billing = null)
    {
        status = status.Replace("\"price_label\":\"$5\",\"price_cents\":500", "\"price_label\":\"$4.99\",\"price_cents\":499")
            .Replace("\"price_label\":\"$10\",\"price_cents\":1000", "\"price_label\":\"$9.99\",\"price_cents\":999")
            .Replace("checkout/buy/e?checkout[custom][device]=x\"", "checkout/buy/e?checkout[custom][device]=x\"" + EssentialsYear)
            .Replace("checkout/buy/c\"", "checkout/buy/c\"" + CompleteYear);
        return billing is null ? status : status.Replace("\"source\":", $"\"billing\":\"{billing}\",\"source\":");
    }

    private static PlansViewModel Shown(string status) => Ui.Run(() =>
    {
        var ask = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
        var plans = new PlansViewModel(ask);
        ask.Plans = plans;
        ask.Show(Relay.ReadStatus(status)!);
        return plans;
    });

    [Fact]
    public void Each_plans_yearly_option_is_read()
    {
        var s = Relay.ReadStatus(WithYearly(Status()))!;
        Assert.Null(s.Plans[0].Yearly);
        Assert.Equal(("$49.99", 4999, 417), (s.Plans[1].Yearly!.Price, s.Plans[1].Yearly!.PriceCents, s.Plans[1].Yearly!.MonthCents));
        Assert.Equal(833, s.Plans[2].Yearly!.MonthCents);
        Assert.Null(Relay.ReadStatus(Status())!.Plans[1].Yearly);
        Assert.True(Relay.ReadStatus(WithYearly(Status("complete", "subscription"), "year"))!.Plan!.Yearly);
        Assert.False(Relay.ReadStatus(WithYearly(Status("complete", "subscription"), "month"))!.Plan!.Yearly);
    }

    [Fact]
    public void The_plans_open_on_yearly_with_the_saving_on_each_card()
    {
        var plans = Shown(WithYearly(Status()));
        Assert.True(plans.ShowBillingSwitch);
        Assert.True(plans.Yearly);
        var (free, essentials, complete) = (plans.Columns[0], plans.Columns[1], plans.Columns[2]);
        Assert.Equal(("$0", ""), (free.Price, free.SaveText));
        Assert.Equal(("$4.17", "$4.99", " · $49.99 billed once a year · ", "save $9.89"), (essentials.Price, essentials.BilledOld, essentials.BilledText, essentials.SaveText));
        Assert.Equal(("$8.33", "save $19.89"), (complete.Price, complete.SaveText));
        Assert.True(complete.Year);

        Ui.Run(() => plans.PickMonthlyCommand.Execute(null));
        Assert.Equal(("$4.99", "", ""), (plans.Columns[1].Price, plans.Columns[1].BilledText, plans.Columns[1].SaveText));
        Assert.False(plans.Columns[2].Year);
    }

    [Fact]
    public void Without_yearly_options_there_is_no_switch()
    {
        var plans = Shown(Status());
        Assert.False(plans.ShowBillingSwitch);
        Assert.Equal("$5", plans.Columns[1].Price);
        Assert.Equal("Paid monthly, by card or PayPal", plans.PaidText);
    }

    [Fact]
    public void A_yearly_plan_is_said_as_one_and_its_cards_stay_yearly()
    {
        var plans = Shown(WithYearly(Status("essentials", "subscription"), "year").Replace("\"price_label\":\"$5\",\"card\"", "\"price_label\":\"$49.99\",\"card\""));
        Assert.False(plans.ShowBillingSwitch);
        Assert.StartsWith("$49.99 a year · answers read", plans.Summary);
        Assert.Contains("2026", plans.NextWhen);
        Ui.Run(() => plans.PickMonthlyCommand.Execute(null));
        Assert.Equal("$8.33", plans.Columns[2].Price);
    }
}
