using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.PlansTests;
using static Rigsight.Tests.App.RelayFailureTests;

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

    // The owner's word (2026-10-28): the trial isn't in the way of buying; buying ends it (the relay does).
    [Fact]
    public void During_the_trial_both_plans_can_be_bought()
    {
        var plans = Shown(WithYearly(Status("complete", "trial", endsAt: DateTime.UtcNow.AddDays(5).ToString("o")).Replace("\"search\":true", "\"search\":true,\"trial\":{\"state\":\"running\",\"plan\":\"complete\"}")));
        Assert.True(plans.CanBuyPlans);
        Assert.True(plans.ShowBillingSwitch);
        Assert.True(plans.Columns[1].CanBuy);
        Assert.True(plans.Columns[2].CanBuy);
        Assert.False(plans.Columns[2].IsMine);
        Assert.False(plans.Columns[0].CanBuy);
    }

    // "You're on Essentials now." stayed under Free after a refund in Lemon Squeezy (2026-10-28).
    [Fact]
    public void A_done_line_goes_once_the_plan_changes_elsewhere()
    {
        var (ask, plans) = Ui.Run(() =>
        {
            var a = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var p = new PlansViewModel(a);
            a.Plans = p;
            a.Show(Relay.ReadStatus(Status("essentials", "subscription"))!);
            p.Done = "You're on Essentials now.";
            return (a, p);
        });
        Ui.Run(() => ask.Show(Relay.ReadStatus(Status("essentials", "subscription"))!));
        Assert.Equal("You're on Essentials now.", plans.Done);
        Ui.Run(() => ask.Show(Relay.ReadStatus(Status())!));
        Assert.Null(plans.Done);
    }

    // Monthly <-> yearly on the same plan from the plan box (approved mock-up, 2026-10-28).
    [Fact]
    public void A_monthly_plan_offers_yearly_with_what_it_saves_and_about_what_is_paid_today()
    {
        var plans = Shown(WithYearly(Status("essentials", "subscription"), "month").Replace("\"price_label\":\"$5\",\"card\"", "\"price_label\":\"$4.99\",\"card\""));
        Assert.True(plans.CanChangeBilling);
        Assert.False(plans.MonthlyNext);
        Assert.Equal(("Switch to yearly", "save $9.89", " · "), (plans.BillingButton, plans.BillingSave, plans.BillingSep));
        Ui.Run(() => plans.AskBillingCommand.Execute(null));
        Assert.True(plans.BillingOpen);
        Assert.Equal("Switch to yearly?", plans.BillingTitle);
        Assert.True(plans.ShowsBillingMoney);
        Assert.Equal("$49.99", plans.BillingYear);
        Assert.StartsWith("Switch and pay $", plans.BillingConfirm);
    }

    [Fact]
    public void A_yearly_plan_offers_monthly_from_its_renewal_with_no_sales_line()
    {
        var plans = Shown(WithYearly(Status("essentials", "subscription"), "year").Replace("\"price_label\":\"$5\",\"card\"", "\"price_label\":\"$49.99\",\"card\""));
        Assert.Equal(("Switch to monthly", "", ""), (plans.BillingButton, plans.BillingSave, plans.BillingSep));
        Ui.Run(() => plans.AskBillingCommand.Execute(null));
        Assert.Equal("Switch to monthly?", plans.BillingTitle);
        Assert.False(plans.ShowsBillingMoney);
        Assert.Contains("Nothing is charged or refunded now.", plans.BillingText);
        Assert.Equal("Switch from 22 November 2026", plans.BillingConfirm);
    }

    [Fact]
    public void Monthly_next_says_the_year_is_paid_and_offers_to_keep_yearly_at_no_charge()
    {
        string renews = DateTime.UtcNow.AddDays(300).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var plans = Shown(WithYearly(Status("essentials", "subscription"), "month").Replace("2026-11-22T10:00:00Z", renews)
            .Replace("\"price_label\":\"$5\",\"card\"", "\"price_label\":\"$4.99\",\"card\""));
        Assert.True(plans.MonthlyNext);
        Assert.StartsWith("$49.99 paid to ", plans.Summary);
        Assert.Contains(" · then $4.99 a month", plans.Summary);
        Assert.Equal(("Keep yearly", "save $9.89"), (plans.BillingButton, plans.BillingSave));
        Ui.Run(() => plans.AskBillingCommand.Execute(null));
        Assert.Equal("Keep paying yearly?", plans.BillingTitle);
        Assert.False(plans.ShowsBillingMoney);
        Assert.Equal("Keep yearly", plans.BillingConfirm);
    }

    [Fact]
    public async Task Switching_billing_asks_the_relay_and_shows_what_it_says()
    {
        string? path = null, body = null;
        await using var relay = new FakeRelay((r, s) =>
        {
            if (r.Path == "/v1/plan/billing") { path = r.Path; body = r.Body; }
            return FakeRelay.Reply(s, 200, "application/json", WithYearly(Status("essentials", "subscription"), r.Path == "/v1/plan/billing" ? "year" : "month"));
        });
        var plans = Shown(WithYearly(Status("essentials", "subscription"), "month"));
        Ui.Run(() =>
        {
            Relay.TestRoot.Value = relay.Url;
            plans.AskBillingCommand.Execute(null);
            plans.ConfirmBillingCommand.Execute(null);
        });
        Assert.True(Ui.WaitFor(() => !plans.BillingOpen && plans.Done is not null, 15_000), "the switch never finished");
        Assert.Equal("/v1/plan/billing", path);
        Assert.Contains("\"billing\":\"year\"", body);
        Assert.Equal("Essentials is paid yearly now.", plans.Done);
        Ui.Run(() => Relay.TestRoot.Value = null);
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
