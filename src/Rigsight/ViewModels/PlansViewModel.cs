using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// One line under a plan: what its answers can now use, the page it comes from (its hover), and the page's icon, in a square
/// of the plan's colour (approved mock-up, 2026-10-24).
/// </summary>
public sealed record PlanUse(string Text, string From, string Icon);

/// <summary>A row of the page by page list: the page, what it holds, its icon, which plans open it, and the plan that adds it.</summary>
public sealed record PlanCompareRow(string Page, string What, bool Free, bool Middle, bool Top, string Icon = "", int Tier = 0);

/// <summary>
/// The page by page list in groups: the pages every plan opens, then what each plan adds, each headed in that plan's colour
/// (<paramref name="Tier"/>: 0 Free, 1 the middle plan, 2 the top one).
/// </summary>
public sealed record PlanCompareGroup(string Title, int Tier, IReadOnlyList<PlanCompareRow> Rows);

/// <summary>A reason picked when cancelling (one tap, optional): its word for the relay and its words on screen.</summary>
public sealed partial class CancelReason(string id, string text) : ObservableObject
{
    public string Id { get; } = id;
    public string Text { get; } = text;
    [ObservableProperty] private bool _picked;
}

/// <summary>One plan's column on the Plans screen (the mock-up approved 2026-10-23): price, questions, web search, what answers use.</summary>
public sealed class PlanColumn(RelayPlanOffer offer, PlansViewModel owner, int rank)
{
    public RelayPlanOffer Offer { get; } = offer;
    public string Name => Offer.Name;
    public string For => rank switch
    {
        0 => Lang.T("Try it on your own PC"),
        1 => Lang.T("Keep an eye on your PC's health, heat, crashes and space"),
        _ => Lang.T("Everything RigSight records, games and network included"),
    };
    public string Price => Offer.IsFree ? "$0" : Year ? PlansViewModel.Dollars(Offer.Yearly!.MonthCents) : Offer.Price;
    public bool Monthly => !Offer.IsFree;

    /// <summary>
    /// Shown for a year (the Yearly switch, or a yearly plan): the price a month, with the real charge under it, the
    /// monthly price crossed out and what it saves (approved mock-up, 2026-10-28).
    /// </summary>
    public bool Year => owner.ShowsYearly && Offer.Yearly is not null;
    public string BilledOld => Year ? Offer.Price : "";
    public string BilledText => Year ? " · " + Lang.T($"{Offer.Yearly!.Price} billed once a year") + " · " : "";
    public string SaveText => Year ? Lang.T($"save {PlansViewModel.Dollars(12 * Offer.PriceCents - Offer.Yearly!.PriceCents)}") : "";
    public string Questions => Offer is { IsFree: true, Cap: 0 } ? Lang.T("Free questions are paused for now")
        : Offer.Weekly ? Lang.T($"{Offer.Cap} questions a week") : Lang.T($"Up to {Offer.Cap} questions a day");
    public string QuestionsWhen => Offer is { IsFree: true, Cap: 0 } ? Lang.T("A plan keeps answering")
        : Offer.Weekly ? Lang.T($"Back to {Offer.Cap} every Monday") : Lang.T("Starts again every midnight");
    public string Everything => rank == 0 ? "" : Lang.T($"Everything in {owner.Columns[rank - 1].Name}, plus");
    public bool HasEverything => rank > 0;

    /// <summary>"Everything in Free" in bold, then ", plus": one line of words, the part that names the plan bold.</summary>
    public string EverythingIn => rank == 0 ? "" : Lang.T($"Everything in {owner.Columns[rank - 1].Name}");

    /// <summary>The plan's place, for its colour: 0 Free (sky), 1 the middle plan (blue), 2 the top one (violet).</summary>
    public int Tier => rank;

    /// <summary>
    /// Two questions only this plan can answer, at the foot of its card where the shorter lists left empty space (approved
    /// mock-up, 2026-10-24), in this PC's parts' names where they're known.
    /// </summary>
    public IReadOnlyList<string> Tries => rank switch
    {
        0 => [Lang.T($"Are my temperatures normal for a {Parts.Cpu}?"), Lang.T($"Is the driver for my {Parts.Gpu} up to date?")],
        1 => [Lang.T("Why did my PC shut off last night?"), Lang.T("What's filling my drives?")],
        _ => [Lang.T("Why does my game stutter since the last driver?"), Lang.T("Why did my internet drop yesterday?")],
    };

    /// <summary>The pages this plan adds to the one before it, each as what an answer can now use.</summary>
    public IReadOnlyList<PlanUse> Uses => [.. AiPages.All
        .Where(p => Offer.Pages.Contains(p.Id) && (rank == 0 || !owner.Columns[rank - 1].Offer.Pages.Contains(p.Id)))
        .Select(p => new PlanUse(Lang.T(Gives(p.Id)), p.Label, PageSwitchesViewModel.Glyph(p.Id)))];

    // The trial isn't a plan of their own: its plan's card still offers to buy it (2026-10-28).
    public bool IsMine => owner.Plan is { } mine && mine.Id == Offer.Id && (Offer.IsFree || !mine.IsFree) && !mine.IsTrial;
    public bool IsBest => rank == owner.Columns.Count - 1 && !IsMine;
    public bool CanBuy => !Offer.IsFree && owner.CanBuyPlans;
    public bool CanSwitch => !Offer.IsFree && !owner.IsFree && !IsMine && owner.Plan is { CanManage: true, Ending: false };
    public string BuyText => Lang.T($"Get {Name}");
    public string SwitchText => Lang.T($"Switch to {Name}");
    /// <summary>At the foot of the page by page list, under the plan's name: "Switch to Essentials" didn't fit its 130 px (2026-10-27).</summary>
    public string BuyShort => Lang.T("Get");
    public string SwitchShort => Lang.T("Switch");
    public string MineText => (Offer.IsFree ? owner.FreeLine : Lang.T("Your plan")) + (owner.HasGiven ? " · " + Lang.T($"{owner.Plan!.Given} given") : "");
    public bool ShowMine => IsMine;
    public bool ShowNothing => !IsMine && !CanBuy && !CanSwitch;
    /// <summary>Free's column while on a plan: what RigSight goes back to, so its button's place isn't left empty.</summary>
    public string NothingText => Offer.IsFree ? Lang.T("What you're back on if a plan ends") : "";

    /// <summary>A page's line as a plan sells it: what an answer can now do, short.</summary>
    public static string Gives(string page) => page switch
    {
        AiPages.Home => Lang.Key("How your PC is doing today"),
        AiPages.Parts => Lang.Key("Your exact parts and drivers"),
        AiPages.Temperatures => Lang.Key("Heat, and when parts slowed down to cool"),
        AiPages.Crashes => Lang.Key("Why it crashed or shut off"),
        AiPages.Memory => Lang.Key("Memory, and apps that keep growing"),
        AiPages.Storage => Lang.Key("Drive space, drive health, what to free"),
        AiPages.Processes => Lang.Key("What's running right now"),
        AiPages.Reports => Lang.Key("Your day, week and month"),
        AiPages.Games => Lang.Key("Frame rates and stutter in each game"),
        AiPages.Timeline => Lang.Key("What got better or worse after an update or driver"),
        AiPages.Apps => Lang.Key("How much you use each app"),
        AiPages.Network => Lang.Key("Internet use and drops"),
        AiPages.Fans => Lang.Key("Fan speeds, and fans wearing out"),
        AiPages.Sensors => Lang.Key("Every sensor reading"),
        AiPages.Rewind => Lang.Key("Your Rewind"),
        AiPages.Widgets => Lang.Key("Which widgets suit you"),
        AiPages.Overlay => Lang.Key("What your game overlay should show"),
        AiPages.Taskbar => Lang.Key("Which taskbar readings to keep"),
        _ => Lang.Key("Your own dashboards"),
    };
}

/// <summary>
/// RigSight AI's plans, for Settings › AI (your plan, up front), the Plans screen over the whole window (choosing and
/// managing), and the Ask window's cards when a limit or a page gets in the way. Everything about money is done through the
/// relay, which alone talks to Lemon Squeezy; the app only shows what the relay says the plan is.
/// </summary>
public sealed partial class PlansViewModel : ObservableObject
{
    private readonly AskViewModel _ask;

    public PlansViewModel(AskViewModel ask)
    {
        _ask = ask;
        _ask.PropertyChanged += Changed;
        Reasons =
        [
            new("too_expensive", Lang.T("Too expensive")), new("not_enough", Lang.T("Didn't use it enough")), new("not_helpful", Lang.T("Answers weren't helpful")),
            new("once", Lang.T("Only needed it once")), new("other", Lang.T("Something else")),
        ];
        Rebuild();
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AskViewModel.Standing) or "")
        {
            if (Done is not null && _doneFor != PlanKey) Done = null;
            Rebuild();
        }
        else if (e.PropertyName is nameof(AskViewModel.CheckingPlan) or nameof(AskViewModel.PlanUnknown))
            foreach (var name in new[] { nameof(Loading), nameof(CantTell), nameof(CantTellText), nameof(ShowPlan) }) OnPropertyChanged(name);
    }

    /// <summary>Monthly or Yearly on the Plans screen: Yearly when it opens (approved mock-up, 2026-10-28).</summary>
    [ObservableProperty] private bool _yearly = true;
    partial void OnYearlyChanged(bool value) => Rebuild();
    [RelayCommand] private void PickMonthly() => Yearly = false;
    [RelayCommand] private void PickYearly() => Yearly = true;

    /// <summary>The relay has a yearly option for some plan (none from an older relay, or before the plans are set up).</summary>
    public bool HasYearly => Columns.Any(c => c.Offer.Yearly is not null);
    /// <summary>The switch shows while choosing; on a plan, the cards show how it's paid (switching keeps it, 2026-10-28).</summary>
    public bool ShowBillingSwitch => HasYearly && !IsSubscription;
    public bool ShowsYearly => IsSubscription ? Plan!.Yearly : Yearly;

    public string PaidText => HasYearly ? Lang.T("Paid monthly or yearly, by card or PayPal") : Lang.T("Paid monthly, by card or PayPal");
    public string AfterCancelText => HasYearly ? Lang.T("After cancelling, it works until the time you paid for ends")
        : Lang.T("After cancelling, it works until the month you paid for ends");

    /// <summary>The refund rule (the owner's word, 2026-10-28): a full refund when support is emailed within 7 days of paying.</summary>
    public string RefundText => Lang.T($"Changed your mind? Email {AiSettingsViewModel.SupportEmail} within 7 days of paying for a full refund");

    internal static string Dollars(int cents) => "$" + (cents / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void Rebuild()
    {
        var offers = _ask.Standing?.Plans is { Count: > 0 } given ? given : Defaults;
        // Each column reads the one before it ("Everything in Free, and:"), so they're added in order.
        var columns = new List<PlanColumn>();
        Columns = columns;
        foreach (var offer in offers.Take(3)) columns.Add(new PlanColumn(offer, this, columns.Count));
        OnPropertyChanged(string.Empty);
    }

    /// <summary>What the plans are before the relay has said (no checkout links: buying waits for it).</summary>
    private static readonly IReadOnlyList<RelayPlanOffer> Defaults =
    [
        new("free", "Free", "$0", 0, 10, "week", AiPages.DefaultFor(0), "") { Search = false },
        new("essentials", "Essentials", "$4.99", 499, 100, "day", AiPages.DefaultFor(1), ""),
        new("complete", "Complete", "$9.99", 999, 300, "day", AiPages.DefaultFor(2), ""),
    ];

    public IReadOnlyList<PlanColumn> Columns { get; private set; } = [];

    // ── The plan now ─────────────────────────────────────────────────────
    public RelayPlan? Plan => _ask.Standing?.Plan;

    /// <summary>
    /// Nothing known and the relay asked: the whole card (plan and licence) waits with a spinner, so Free never shows
    /// before the real plan. Couldn't ask: says so with Try again, in place of a guess.
    /// </summary>
    /// <remarks>Not only while a check is in flight: the moment before one starts showed Free for a second or two, and a
    /// press of See plans then opened Free's plans on a Complete PC (2026-10-26). Unknown and not failed is loading.</remarks>
    public bool Loading => _ask.ShowQuestionsLeft && _ask.Standing is null && _ask.PlanUnknown is null;
    public bool CantTell => !Loading && _ask.Standing is null && _ask.PlanUnknown is not null;
    public string CantTellText => _ask.PlanUnknown ?? "";
    public bool ShowPlan => !Loading && !CantTell;

    [RelayCommand] private Task CheckPlanAgain() => _ask.RefreshQuestionsAsync(theyAsked: true);
    /// <summary>Known: the relay has said where this PC stands (it says nothing until RigSight AI is picked and used).</summary>
    public bool Known => Plan is not null;
    public bool IsFree => Plan?.IsFree ?? true;
    /// <summary>
    /// Plans can be bought: on Free, or during the free trial (the owner's word, 2026-10-28: buying ends the trial and the
    /// plan starts; the relay does that).
    /// </summary>
    public bool CanBuyPlans => IsFree || Plan is { IsTrial: true };
    public bool IsPaid => Plan is { IsFree: false };
    /// <summary>Given by support, or the free trial: nothing to pay and nothing to manage.</summary>
    public bool IsGift => Plan is { IsFree: false } p && (p.Gift || p.IsTrial);
    public bool IsSubscription => IsPaid && !IsGift;
    public bool IsEnding => Plan is { Ending: true, IsFree: false };
    public bool IsPastDue => Plan is { PastDue: true, IsFree: false };
    public bool CanManage => Plan is { CanManage: true, IsFree: false };

    public string PlanName => Plan?.Name ?? Lang.T("Free");
    public string StateText => Plan switch
    {
        { IsFree: true, Cap: 0 } => Lang.T("Paused for now"),
        { IsFree: true } p => p.Left == 1 && p.Cap == 10 ? Lang.T("1 of 10 left this week") : Lang.T($"{p.Left} of {p.Cap} left this week"),
        { Gift: true } => Lang.T("Given by RigSight"),
        { IsTrial: true, EndsAt: { } trialEnd } => TrialLeft(trialEnd),
        { Ending: true, EndsAt: { } end } => Lang.T($"Ends {TimeText.At(end, "d MMMM")}"),
        { PastDue: true } => Lang.T("Payment didn't go through"),
        not null => Lang.T("Active"),
        _ => Lang.T("Free"),
    };
    public bool StateIsWarning => IsEnding || IsPastDue;

    /// <summary>"7 of 10 left this week" on Free; the free column's own line on the Plans screen.</summary>
    public string FreeLine => Plan is { IsFree: true } p ? (FreePaused ? StateText : p.Left == 0 ? Lang.T("All 10 used this week") : StateText) : Lang.T("10 questions a week");

    /// <summary>
    /// The free trial on offer, on Free's box in Settings (the owner's word, 2026-10-28: Free said only "10 of 10 left
    /// this week", so no one knew a trial was waiting). Gone once it has started or ended, or the relay offers none.
    /// </summary>
    public string TrialLine => _ask.Standing is { Trial: { Offered: true } t, Plan.IsFree: true } s && t.Days > 0
        ? Lang.T($"Try {s.Plans.FirstOrDefault(p => p.Id == t.Plan)?.Name ?? "Complete"} free for {t.Days} days. It starts with your first question. No card, nothing to cancel.")
        : "";
    public bool HasTrialLine => TrialLine.Length > 0;

    /// <summary>Free questions paused on the relay (none this week for anyone): said so, never "0 of 0".</summary>
    public bool FreePaused => Plan is { IsFree: true, Cap: 0 };

    // When they come back, and no more: the sales line on every Free card went (the wording audit, 2026-10-24); the
    // plans themselves are one press away.
    public string FreeWhen => FreePaused ? Lang.T("Free questions are paused for now.")
        : Plan?.ResetsAt is { } at ? Lang.T($"Back to {Plan.Cap} on {TimeText.At(at, "dddd")}.")
        : "";

    // What it reads and does, said plainly (audit item 4, 2026-10-29: "Answers read all 19 · web search" didn't say 19 of what).
    public string Summary => Plan is not { IsFree: false } p ? ""
        : (p.Gift || p.IsTrial ? Lang.T($"Reads {PagesText(p.Pages.Count)}")
           : p.Yearly ? Lang.T($"{p.Price} a year · reads {PagesText(p.Pages.Count)}")
           : MonthlyNext && MineColumn?.Offer.Yearly is { } paid && p.RenewsAt is { } yearEnd
             ? Lang.T($"{paid.Price} paid to {TimeText.At(yearEnd, "d MMMM yyyy")} · then {p.Price} a month")
           : Lang.T($"{p.Price} a month · reads {PagesText(p.Pages.Count)}"))
          + (PlanSearches ? " · " + Lang.T("searches the web") : "");

    /// <summary>The trial's chip: "Free trial · 7 days left", "Free trial · ends today", once (its end is said under the card).</summary>
    internal static string TrialLeft(DateTime ends, DateTime? now = null)
    {
        var at = now ?? DateTime.Now;
        int days = (int)Math.Ceiling((ends - at).TotalDays);
        return ends.Date == at.Date || days <= 0 ? Lang.T("Free trial · ends today")
            : days == 1 ? Lang.T("Free trial · 1 day left") : Lang.T($"Free trial · {days} days left");
    }

    /// <summary>The plan now searches the web (Free doesn't, 2026-10-27).</summary>
    public bool PlanSearches => Plan is { } now && Columns.FirstOrDefault(c => c.Offer.Id == now.Id)?.Offer.Search is not false && !now.IsFree;

    /// <summary>The plans that search the web, by name: "Every plan searches the web", or "Essentials and Complete search the web".</summary>
    public string WebHeading
    {
        get
        {
            var names = Columns.Where(c => c.Offer.Search).Select(c => c.Offer.Name).ToList();
            return names.Count == Columns.Count ? Lang.T("Every plan searches the web")
                : names.Count == 0 ? Lang.T("Web search")
                : Lang.T($"{string.Join(" " + Lang.T("and") + " ", names)} search the web");
        }
    }

    private static string PagesText(int n) => n >= AiPages.All.Count ? Lang.T($"all {AiPages.All.Count} pages") : Lang.T($"{n} of {AiPages.All.Count} pages");

    public double UsedPart => Plan is { Cap: > 0 } p ? Math.Clamp((double)p.Used / p.Cap, 0, 1) : 0;

    // Questions given from the admin site (the person's rule, 2026-10-25): kept until used, after the plan's own, so a
    // line of their own under the day's or week's bar, saying who gave them, for as long as any are left.
    public bool HasGiven => Plan is { Given: > 0 };
    public string GivenText => Plan is not { Given: > 0 } p ? ""
        : p.IsFree ? Lang.T($"{p.Given} given by RigSight · used after this week's {p.Cap}, they don't run out")
        : Lang.T($"{p.Given} given by RigSight · used after {p.Name}'s {p.Cap} a day, they don't run out");
    public string UsedText => Plan is not { } p ? ""
        : p.Weekly ? Lang.T($"{p.Used} of {p.Cap} free questions used this week")
        : Lang.T($"{p.Used} of {p.Cap} questions used today · starts again at midnight");
    public string LeftToday => Plan is { } p ? p.Left.ToString("N0") : "";
    public string LeftLabel => Plan is { Weekly: true } ? Lang.T("left this week") : Lang.T("left today");

    public string NextPayment => Plan is { RenewsAt: { } on } p && !p.Ending ? Lang.T($"{p.Price} on {TimeText.At(on, "d MMMM")}") : "";
    public bool HasNextPayment => IsSubscription && !IsEnding && NextPayment.Length > 0;
    // In place of the old "Receipts: every payment" line (the person's call, 2026-10-24: say something worth knowing).
    public string LastPayment => Plan?.LastPayment is { } last ? Lang.T($"{last.Amount} on {TimeText.At(last.At, "d MMMM")}") : "";

    // The facts as the plan box draws them: the amount in bold, when under it (approved mock-up 2026-10-24).
    public string NextAmount => Plan?.Price ?? "";
    // A yearly plan's next payment is months off, so its year is said.
    public string NextWhen => Plan is { RenewsAt: { } on } p ? Lang.T($"on {TimeText.At(on, p.Yearly || MonthlyNext ? "d MMMM yyyy" : "d MMMM")}") + " · " + In(on) : "";
    public string LastAmount => Plan?.LastPayment?.Amount ?? "";
    public string LastWhen => Plan?.LastPayment is { } last ? Lang.T($"on {TimeText.At(last.At, "d MMMM")}") : "";
    public string CardBrand => (Plan?.Card?.Brand ?? "").ToUpperInvariant();
    public bool HasCard => Plan?.Card is not null;
    public string CardDigits => Plan?.Card is { } c ? "•••• " + c.Last4 : Lang.T("Your card");

    /// <summary>"in 31 days", "tomorrow", "today": how far off, so no one counts days.</summary>
    internal static string In(DateTime on)
    {
        int days = (on.Date - DateTime.Today).Days;
        return days <= 0 ? Lang.T("today") : days == 1 ? Lang.T("tomorrow") : Lang.T($"in {days} days");
    }
    public bool HasLastPayment => IsSubscription && LastPayment.Length > 0;
    public bool CanCancel => CanManage && HasNextPayment;
    public string EndingText => Plan is { EndsAt: { } end } p
        ? Lang.T($"Cancelled. {p.Name} works until {TimeText.At(end, "d MMMM")}, and you won't be charged again. Then RigSight goes back to Free.") : "";
    public string KeepText => Lang.T($"Keep {PlanName}");
    public string GiftText => Plan is { IsTrial: true, EndsAt: { } trialEnds }
        ? Lang.T($"Ends {TimeText.At(trialEnds, "ddd d MMM")}, {TimeText.Time(trialEnds)}. Nothing to pay; then back to Free.")
        : Plan is { Gift: true } p
        ? p.EndsAt is { } end ? Lang.T($"RigSight gave this PC {p.Name} until {TimeText.At(end, "d MMMM yyyy")}. Nothing to pay.") : Lang.T($"RigSight gave this PC {p.Name}. Nothing to pay.")
        : "";
    public string CardText => Plan?.Card?.Text ?? Lang.T("Your card");
    public string PastDueText => Lang.T($"Your last {PlanName} payment didn't go through. Lemon Squeezy tries again over the next days, and {PlanName} keeps working meanwhile. Change the card to sort it now.");

    /// <summary>The other paid plan, for "Switch to …" (going up from the cheaper one, down from the dearer).</summary>
    public PlanColumn? Other => Columns.Where(c => !c.Offer.IsFree && Plan is { } p && c.Offer.Id != p.Id).LastOrDefault();
    /// <summary>
    /// "Change plan": one neutral button to the plans screen, where switching happens and says what it costs (the
    /// person's call, 2026-10-24: no button that names a downgrade and invites it; the change stays one press away, as
    /// Spotify's "Change plan").
    /// </summary>
    public bool CanSwitchPlan => CanManage && !IsEnding && Other is not null;
    /// <summary>Not on the plans screen itself, where the plans are already shown.</summary>
    public bool ShowChangePlan => CanSwitchPlan && !IsOpen;
    public string OtherText => Other is { } o ? Lang.T($"{o.Name} · answers read {PagesText(o.Offer.Pages.Count)}") : "";
    public bool SwitchGoesUp => Other is { } o && Plan is { } p && Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is { } mine && o.Offer.PriceCents > mine.Offer.PriceCents;

    // ── The Plans screen ────────────────────────────────────────────────
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowChangePlan))] private bool _isOpen;


    public string Heading => IsFree ? Lang.T("Ask RigSight about anything on your PC") : Lang.T("Your RigSight AI plan");
    public string Subheading => IsFree ? Lang.T("Answers come from what RigSight recorded on this PC.")
        : Lang.T("Switch, cancel or change your card here. Changes take effect straight away.");

    public IReadOnlyList<PlanCompareRow> CompareRows => Columns.Count < 3 ? []
        : [.. AiPages.All.Select(p =>
            {
                bool free = Columns[0].Offer.Pages.Contains(p.Id), middle = Columns[1].Offer.Pages.Contains(p.Id), top = Columns[2].Offer.Pages.Contains(p.Id);
                return new PlanCompareRow(p.Label, p.Description, free, middle, top, PageSwitchesViewModel.Glyph(p.Id), free ? 0 : middle ? 1 : 2);
            })];

    /// <summary>
    /// The page by page list, always shown (the person's call, 2026-10-24: no Compare button), grouped: what every plan opens,
    /// then what each plan adds, so what a plan is for reads at a glance. A group with no pages is left out.
    /// </summary>
    public IReadOnlyList<PlanCompareGroup> CompareGroups
    {
        get
        {
            if (Columns.Count < 3) return [];
            var rows = CompareRows;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            return [.. new[]
            {
                new PlanCompareGroup(Lang.T("In every plan").ToUpper(culture), 0, [.. rows.Where(r => r.Tier == 0)]),
                new PlanCompareGroup(Lang.T($"{Columns[1].Name} adds").ToUpper(culture), 1, [.. rows.Where(r => r.Tier == 1)]),
                new PlanCompareGroup(Lang.T($"{Columns[2].Name} adds").ToUpper(culture), 2, [.. rows.Where(r => r.Tier == 2)]),
            }.Where(g => g.Rows.Count > 0)];
        }
    }

    public string WebExampleHeat => Lang.T($"Finds the safe range for your {Parts.Gpu} and compares it with yours.");

    /// <summary>Opened from Settings (See plans, Compare plans), or an Ask card; Back returns there.</summary>
    public void Open()
    {
        Problem = null;
        IsOpen = true;
        WantsFront?.Invoke();
        _ = _ask.RefreshQuestionsAsync(theyAsked: true);
    }

    [RelayCommand] private void Show() => Open();
    [RelayCommand] private void Back() => IsOpen = false;

    /// <summary>Why the last action didn't work (shown where it was pressed), or null.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string? _problem;
    public bool HasProblem => Problem is not null;

    [ObservableProperty] private string? _done;

    /// <summary>
    /// The plan as it was when <see cref="Done"/> was said: once the plan changes without a press here (a refund or a
    /// cancel in Lemon Squeezy, an admin change), the line goes. It said "You're on Essentials now." under Free (2026-10-28).
    /// </summary>
    private string? _doneFor;
    partial void OnDoneChanged(string? value) => _doneFor = value is null ? null : PlanKey;
    private string PlanKey => Plan is { } p ? $"{p.Id}|{p.Source}|{p.State}" : "";

    // ── Buying ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _waiting;
    private CancellationTokenSource? _wait;
    private static readonly TimeSpan BuyWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Opens the plan's checkout (Lemon Squeezy, in the browser; the link carries this PC's id) and waits for the relay to
    /// say this PC is on it. True once it is; the Ask window then sends the question that hit the limit.
    /// </summary>
    /// <remarks><paramref name="yearly"/>: the plan's yearly checkout (the Plans screen's switch); an Ask card says a month's price, so buys monthly.</remarks>
    public async Task<bool> BuyAsync(string planId, bool yearly = false)
    {
        Problem = null;
        if (_ask.Standing?.Plans is not { Count: > 0 } && await _ask.RefreshQuestionsAsync(theyAsked: true) is { } unreachable) { Problem = unreachable; return false; }
        var offer = _ask.Standing?.Plans.FirstOrDefault(p => p.Id == planId);
        string? checkout = yearly ? offer?.Yearly?.Url : offer?.Url;
        if (offer is null || !Uri.TryCreate(checkout, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            Log.Write("plans", $"Get {planId} ({(yearly ? "yearly" : "monthly")}): the relay has no checkout for it yet");
            Problem = Lang.T("Buying isn't open yet. Try again later.");
            return false;
        }
        Log.Write("plans", $"Opening the {(yearly ? "yearly" : "monthly")} checkout for {planId}");
        if (!Browser.Open(checkout)) { Problem = Lang.T("Your browser couldn't be opened. Try again."); return false; }
        StopWaiting();
        var cts = _wait = new CancellationTokenSource(BuyWait);
        Waiting = true;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                var (status, problem) = await Relay.StatusAsync(cts.Token);
                if (status is null) { Log.Write("plans", $"Waiting for {planId}: {problem}"); continue; }
                // The trial's own plan isn't bought yet: wait for the subscription.
                if (status.Plan?.Id != planId || status.Plan.IsTrial) continue;
                Log.Write("plans", $"This PC is on {planId} now (bought, or given from the admin site)");
                _ask.Show(status);
                Done = Lang.T($"You're on {status.Plan.Name} now.");
                IsOpen = false;
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Write("plans", $"Stopped waiting for {planId} (cancelled, or {BuyWait.TotalMinutes:0} minutes passed)");
            return false;
        }
        finally
        {
            if (_wait == cts) { _wait = null; Waiting = false; }
            cts.Dispose();
        }
    }

    [RelayCommand] private async Task Buy(PlanColumn? column) { if (column is not null) await BuyAsync(column.Offer.Id, column.Year); }
    /// <summary>Stops waiting for a plan bought in the browser (its button, and the AI turned off or answering another way).</summary>
    [RelayCommand] public void StopWaiting() => _wait?.Cancel();

    // ── Cancel, keep, switch ────────────────────────────────────────────
    public IReadOnlyList<CancelReason> Reasons { get; }
    [ObservableProperty] private bool _cancelOpen;
    public string CancelTitle => Lang.T($"Cancel {PlanName}?");
    public string CancelText => Plan is { RenewsAt: { } on } ? Lang.T($"You won't be charged again. {PlanName} keeps working until {TimeText.At(on, "d MMMM")}, then you're back on 10 free questions a week.")
        : Plan is { Yearly: true } ? Lang.T($"You won't be charged again. {PlanName} keeps working until the year you paid for ends, then you're back on 10 free questions a week.")
        : Lang.T($"You won't be charged again. {PlanName} keeps working until the month you paid for ends, then you're back on 10 free questions a week.");

    [RelayCommand] private void AskCancel() { Problem = null; foreach (var r in Reasons) r.Picked = false; CancelOpen = true; }
    [RelayCommand] private void Pick(CancelReason? reason) { foreach (var r in Reasons) r.Picked = r == reason && !r.Picked; }
    [RelayCommand]
    private void CloseDialogs()
    {
        CancelOpen = false;
        SwitchOpen = false;
        BillingOpen = false;
        _switching?.TrySetResult(false);
        _switching = null;
    }

    /// <summary>The main window should come in front (the Plans screen or a dialog opened from the Ask window); set by the window.</summary>
    public event Action? WantsFront;

    private TaskCompletionSource<bool>? _switching;

    /// <summary>
    /// From an Ask card: the switch dialog, asked as from the Plans screen (a card never charges a card by itself). True
    /// once the switch went through.
    /// </summary>
    public Task<bool> SwitchNowAsync(string planId)
    {
        _switching?.TrySetResult(false);
        var to = Columns.FirstOrDefault(c => c.Offer.Id == planId);
        if (to is null) return Task.FromResult(false);
        _switching = new TaskCompletionSource<bool>();
        AskSwitch(to);
        WantsFront?.Invoke();
        return _switching.Task;
    }

    [RelayCommand]
    private async Task ConfirmCancel()
    {
        Problem = null;
        var (status, problem) = await Relay.CancelPlanAsync(Reasons.FirstOrDefault(r => r.Picked)?.Id);
        if (status is null) { Problem = problem; return; }
        _ask.Show(status);
        CancelOpen = false;
        Done = Lang.T($"{status.Plan?.Name} cancelled. It works until {(status.Plan?.EndsAt is { } end ? TimeText.At(end, "d MMMM") : "the paid month ends")}.");
    }

    [RelayCommand]
    private async Task Keep()
    {
        Problem = null;
        var (status, problem) = await Relay.KeepPlanAsync();
        if (status is null) { Problem = problem; return; }
        _ask.Show(status);
        Done = Lang.T($"{status.Plan?.Name} kept.");
    }

    [ObservableProperty] private bool _switchOpen;
    [ObservableProperty] private PlanColumn? _switchTo;
    public string SwitchTitle => SwitchTo is { } to ? Lang.T($"Switch to {to.Name}?") : "";
    public string SwitchConfirm => SwitchTo is { } to ? (Up(to) ? Lang.T("Switch and pay the difference") : Lang.T($"Switch to {to.Name}")) : "";

    /// <summary>
    /// What switching costs, in the plan's own words. Going up, Lemon Squeezy charges the part-month difference straight away;
    /// going down, it comes off the next payment. The app's figure is an estimate (Lemon Squeezy works out the real one).
    /// </summary>
    public string SwitchText2 => SwitchTo is not { } to || Plan is not { } p ? ""
        // A yearly plan moves to the other plan's yearly option (the relay keeps it yearly).
        : p.Yearly && to.Offer.Yearly is { } year
            ? Up(to) && p.RenewsAt is { } yearOn
                ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this year now (about {Estimate(to)}), then {year.Price} a year from {TimeText.At(yearOn, "d MMMM yyyy")}.")
                : Up(to) ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this year now (about {Estimate(to)}), then {year.Price} a year.")
                : Lang.T($"It starts now. Answers read {PagesText(to.Offer.Pages.Count)}, with up to {to.Offer.Cap} questions a day. What's left of this year comes off your next payment.")
        : Up(to) && p.RenewsAt is { } on
            ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this month now (about {Estimate(to)}), then {to.Offer.Price} a month from {TimeText.At(on, "d MMMM")}.")
        : Up(to)
            ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this month now (about {Estimate(to)}), then {to.Offer.Price} a month.")
            : Lang.T($"It starts now. Answers read {PagesText(to.Offer.Pages.Count)}, with up to {to.Offer.Cap} questions a day. What's left of this month comes off your next payment.");

    private bool Up(PlanColumn to) => Plan is { } p && Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is { } mine && to.Offer.PriceCents > mine.Offer.PriceCents;

    private string Estimate(PlanColumn to)
    {
        if (Plan is not { RenewsAt: { } renews } p || Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is not { } mine) return to.Offer.Price;
        // The share of this billing month still to come, from its own length: over 30 days it said "about $5.20" for a $5
        // difference on the day the plan was bought (31 days left of a 31-day month, divided by 30; 2026-10-24).
        bool year = p.Yearly && to.Offer.Yearly is not null && mine.Offer.Yearly is not null;
        double month = (renews - (year ? renews.AddYears(-1) : renews.AddMonths(-1))).TotalDays;
        double left = Math.Clamp((renews - DateTime.Now).TotalDays / month, 0, 1);
        int difference = year ? to.Offer.Yearly!.PriceCents - mine.Offer.Yearly!.PriceCents : to.Offer.PriceCents - mine.Offer.PriceCents;
        double dollars = difference / 100.0 * left;
        // To the 10 cents: Lemon Squeezy works out the exact figure, so a precise-looking one would mislead.
        return "$" + (Math.Round(Math.Max(0, dollars) * 10) / 10).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void AskSwitch(PlanColumn? to)
    {
        Problem = null;
        SwitchTo = to ?? Other;
        if (SwitchTo is null) return;
        SwitchOpen = true;
        OnPropertyChanged(nameof(SwitchTitle));
        OnPropertyChanged(nameof(SwitchText2));
        OnPropertyChanged(nameof(SwitchConfirm));
    }

    [RelayCommand]
    private async Task ConfirmSwitch()
    {
        if (SwitchTo is not { } to) return;
        Problem = null;
        var (status, problem) = await Relay.SwitchPlanAsync(to.Offer.Id);
        if (status is null) { Problem = problem; return; }
        _ask.Show(status);
        SwitchOpen = false;
        _switching?.TrySetResult(true);
        _switching = null;
        Done = Lang.T($"You're on {status.Plan?.Name} now.");
    }

    // ── Monthly <-> yearly on the same plan (approved mock-up, 2026-10-28) ──────────────
    private PlanColumn? MineColumn => Plan is { } p ? Columns.FirstOrDefault(c => c.Offer.Id == p.Id) : null;

    /// <summary>
    /// Paid monthly but a paid year still runs: switched to monthly, which starts at the year's end (the relay leaves the
    /// renewal where the year ends, so a monthly plan renewing over 40 days away is one).
    /// </summary>
    public bool MonthlyNext => Plan is { Yearly: false, RenewsAt: { } on } p && !p.Ending && IsSubscription && on > DateTime.Now.AddDays(40);

    /// <summary>The button beside Change plan: while the plan renews and it has a yearly option.</summary>
    public bool CanChangeBilling => CanManage && IsSubscription && !IsEnding && MineColumn?.Offer.Yearly is not null;
    public string BillingButton => Plan is { Yearly: true } ? Lang.T("Switch to monthly") : MonthlyNext ? Lang.T("Keep yearly") : Lang.T("Switch to yearly");
    /// <summary>"save $9.89" beside it, in green, toward yearly only.</summary>
    public string BillingSave => Plan is { Yearly: true } || MineColumn is not { Offer.Yearly: { } y } mine ? "" : Lang.T($"save {Dollars(12 * mine.Offer.PriceCents - y.PriceCents)}");
    public bool HasBillingSave => BillingSave.Length > 0;
    public string BillingSep => HasBillingSave ? " · " : "";

    [ObservableProperty] private bool _billingOpen;
    private bool ToYearly => Plan is not { Yearly: true };
    public string BillingTitle => ToYearly ? (MonthlyNext ? Lang.T("Keep paying yearly?") : Lang.T("Switch to yearly?")) : Lang.T("Switch to monthly?");
    public string BillingText
    {
        get
        {
            if (Plan is not { } p || MineColumn is not { Offer.Yearly: { } y } mine) return "";
            string renews = p.RenewsAt is { } on ? TimeText.At(on, "d MMMM yyyy") : "";
            if (!ToYearly) return Lang.T($"Your year runs to {renews} as paid. After that it's {mine.Offer.Price} a month. Nothing is charged or refunded now.");
            if (MonthlyNext) return Lang.T($"Your year runs to {renews} as paid, then renews at {y.Price} a year. Nothing is charged now.");
            return Lang.T($"{p.Name} stays as it is. You pay for a year now, less what's left of this month, then {y.Price} a year.");
        }
    }

    /// <summary>To yearly from monthly: the year, what comes back for the rest of the month, and about what's paid today.</summary>
    public bool ShowsBillingMoney => ToYearly && !MonthlyNext;
    public string BillingYear => MineColumn?.Offer.Yearly?.Price ?? "";
    public string BillingBack => "−" + Dollars(MonthLeftCents);
    public string BillingToday => Dollars(Math.Max(0, (MineColumn?.Offer.Yearly?.PriceCents ?? 0) - MonthLeftCents));
    public string BillingConfirm => !ToYearly ? Lang.T($"Switch from {(Plan?.RenewsAt is { } on ? TimeText.At(on, "d MMMM yyyy") : "")}")
        : MonthlyNext ? Lang.T("Keep yearly") : Lang.T($"Switch and pay {BillingToday}");

    /// <summary>What's left of this month's payment, rounded to the 10 cents as the switch's estimate is (Lemon Squeezy works out the real one).</summary>
    private int MonthLeftCents
    {
        get
        {
            if (Plan is not { RenewsAt: { } renews } || MineColumn is not { } mine) return 0;
            double month = (renews - renews.AddMonths(-1)).TotalDays;
            double left = Math.Clamp((renews - DateTime.Now).TotalDays / month, 0, 1);
            return (int)(Math.Round(mine.Offer.PriceCents * left / 10.0) * 10);
        }
    }

    [RelayCommand]
    private void AskBilling()
    {
        Problem = null;
        if (!CanChangeBilling) return;
        BillingOpen = true;
        foreach (var name in new[] { nameof(BillingTitle), nameof(BillingText), nameof(ShowsBillingMoney), nameof(BillingYear), nameof(BillingBack), nameof(BillingToday), nameof(BillingConfirm) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private async Task ConfirmBilling()
    {
        Problem = null;
        bool yearly = ToYearly;
        var (status, problem) = await Relay.SetBillingAsync(yearly);
        if (status is null) { Problem = problem; return; }
        _ask.Show(status);
        BillingOpen = false;
        Done = status.Plan is { Yearly: true } ? Lang.T($"{status.Plan.Name} is paid yearly now.")
            : status.Plan?.RenewsAt is { } on ? Lang.T($"Monthly from {TimeText.At(on, "d MMMM yyyy")}.") : Lang.T("Monthly from your next payment.");
    }

    [RelayCommand] private Task ChangeCard() => OpenBilling("card");
    [RelayCommand] private Task Receipts() => OpenBilling("receipts");

    private async Task OpenBilling(string which)
    {
        Problem = null;
        var (url, problem) = await Relay.BillingPageAsync(which);
        if (url is null) { Problem = problem; return; }
        if (!Browser.Open(url)) Problem = Lang.T("Your browser couldn't be opened. Try again.");
    }
}
