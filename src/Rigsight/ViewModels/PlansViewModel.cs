using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One line under a plan: what its answers can now use, with the page it comes from in small grey.</summary>
public sealed record PlanUse(string Text, string From);

/// <summary>A row of "Compare plans page by page": the page, what it holds, and which plans open it.</summary>
public sealed record PlanCompareRow(string Page, string What, bool Free, bool Middle, bool Top);

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
    public string Price => Offer.IsFree ? "$0" : Offer.Price;
    public bool Monthly => !Offer.IsFree;
    public string Questions => Offer is { IsFree: true, Cap: 0 } ? Lang.T("Free questions are paused for now")
        : Offer.Weekly ? Lang.T($"{Offer.Cap} questions a week") : Lang.T($"Up to {Offer.Cap} questions a day");
    public string QuestionsWhen => Offer is { IsFree: true, Cap: 0 } ? Lang.T("A plan keeps answering")
        : Offer.Weekly ? Lang.T($"Back to {Offer.Cap} every Monday") : Lang.T("Starts again every midnight");
    public string Everything => rank == 0 ? "" : Lang.T($"Everything in {owner.Columns[rank - 1].Name}, and:");
    public bool HasEverything => rank > 0;

    /// <summary>The pages this plan adds to the one before it, each as what an answer can now use.</summary>
    public IReadOnlyList<PlanUse> Uses => [.. AiPages.All
        .Where(p => Offer.Pages.Contains(p.Id) && (rank == 0 || !owner.Columns[rank - 1].Offer.Pages.Contains(p.Id)))
        .Select(p => new PlanUse(Lang.T(Gives(p.Id)), p.Label))];

    public bool IsMine => owner.Plan is { } mine && mine.Id == Offer.Id && (Offer.IsFree || !mine.IsFree);
    public bool IsBest => rank == owner.Columns.Count - 1 && !IsMine;
    public bool CanBuy => !Offer.IsFree && owner.IsFree;
    public bool CanSwitch => !Offer.IsFree && !owner.IsFree && !IsMine && owner.Plan is { CanManage: true, Ending: false };
    public string BuyText => Lang.T($"Get {Name}");
    public string SwitchText => Lang.T($"Switch to {Name}");
    public string MineText => Offer.IsFree ? owner.FreeLine : Lang.T("Your plan");
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
        if (e.PropertyName is nameof(AskViewModel.Standing) or "") Rebuild();
    }

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
        new("free", "Free", "$0", 0, 10, "week", AiPages.DefaultFor(0), ""),
        new("essentials", "Essentials", "$5", 500, 100, "day", AiPages.DefaultFor(1), ""),
        new("complete", "Complete", "$10", 1000, 500, "day", AiPages.DefaultFor(2), ""),
    ];

    public IReadOnlyList<PlanColumn> Columns { get; private set; } = [];

    // ── The plan now ─────────────────────────────────────────────────────
    public RelayPlan? Plan => _ask.Standing?.Plan;
    /// <summary>Known: the relay has said where this PC stands (it says nothing until RigSight AI is picked and used).</summary>
    public bool Known => Plan is not null;
    public bool IsFree => Plan?.IsFree ?? true;
    public bool IsPaid => Plan is { IsFree: false };
    public bool IsGift => Plan is { Gift: true, IsFree: false };
    public bool IsSubscription => IsPaid && !IsGift;
    public bool IsEnding => Plan is { Ending: true, IsFree: false };
    public bool IsPastDue => Plan is { PastDue: true, IsFree: false };
    public bool CanManage => Plan is { CanManage: true, IsFree: false };

    public string PlanName => Plan?.Name ?? Lang.T("Free");
    public string StateText => Plan switch
    {
        { IsFree: true, Cap: 0 } => Lang.T("Paused for now"),
        { IsFree: true } p => p.Left == 1 ? Lang.T("1 of 10 left this week") : Lang.T($"{p.Left} of {p.Cap} left this week"),
        { Gift: true } => Lang.T("Given by RigSight"),
        { Ending: true, EndsAt: { } end } => Lang.T($"Ends {TimeText.At(end, "d MMMM")}"),
        { PastDue: true } => Lang.T("Payment didn't go through"),
        not null => Lang.T("Active"),
        _ => Lang.T("Free"),
    };
    public bool StateIsWarning => IsEnding || IsPastDue;

    /// <summary>"7 of 10 left this week" on Free; the free column's own line on the Plans screen.</summary>
    public string FreeLine => Plan is { IsFree: true } p ? (FreePaused ? StateText : p.Left == 0 ? Lang.T("All 10 used this week") : StateText) : Lang.T("10 questions a week");

    /// <summary>Free questions paused on the relay (none this week for anyone): said so, never "0 of 0".</summary>
    public bool FreePaused => Plan is { IsFree: true, Cap: 0 };

    public string FreeWhen => FreePaused ? Lang.T("Free questions are paused for now. Plans give up to 500 questions a day, and answers that use more of your PC's records.")
        : Plan?.ResetsAt is { } at
        ? Lang.T($"Back to {Plan.Cap} on {TimeText.At(at, "dddd")}. Plans give up to 500 questions a day, and answers that use more of your PC's records.")
        : Lang.T("Plans give up to 500 questions a day, and answers that use more of your PC's records.");

    public string Summary => Plan is not { IsFree: false } p ? ""
        : p.Gift ? Lang.T($"Answers use {PagesText(p.Pages.Count)} · web search")
        : Lang.T($"{p.Price} a month · answers use {PagesText(p.Pages.Count)} · web search");

    private static string PagesText(int n) => n >= AiPages.All.Count ? Lang.T($"all {AiPages.All.Count} pages") : Lang.T($"{n} of {AiPages.All.Count} pages");

    public double UsedPart => Plan is { Cap: > 0 } p ? Math.Clamp((double)p.Used / p.Cap, 0, 1) : 0;
    public string UsedText => Plan is not { } p ? ""
        : p.Weekly ? Lang.T($"{p.Used} of {p.Cap} free questions used this week")
        : Lang.T($"{p.Used} of {p.Cap} questions used today · starts again at midnight");
    public string LeftToday => Plan is { } p ? p.Left.ToString("N0") : "";
    public string LeftLabel => Plan is { Weekly: true } ? Lang.T("left this week") : Lang.T("left today");

    public string NextPayment => Plan is { RenewsAt: { } on } p && !p.Ending ? Lang.T($"{p.Price} on {TimeText.At(on, "d MMMM")}") : "";
    public bool HasNextPayment => IsSubscription && !IsEnding && NextPayment.Length > 0;
    public string EndingText => Plan is { EndsAt: { } end } p
        ? Lang.T($"Cancelled. {p.Name} works until {TimeText.At(end, "d MMMM")}, and you won't be charged again. Then RigSight goes back to Free.") : "";
    public string KeepText => Lang.T($"Keep {PlanName}");
    public string GiftText => Plan is { Gift: true } p
        ? p.EndsAt is { } end ? Lang.T($"RigSight gave this PC {p.Name} until {TimeText.At(end, "d MMMM yyyy")}. Nothing to pay.") : Lang.T($"RigSight gave this PC {p.Name}. Nothing to pay.")
        : "";
    public string CardText => Plan?.Card?.Text ?? Lang.T("Your card");
    public string PastDueText => Lang.T($"Your last {PlanName} payment didn't go through. Lemon Squeezy tries again over the next days, and {PlanName} keeps working meanwhile. Change the card to sort it now.");

    /// <summary>The other paid plan, for "Switch to …" (going up from the cheaper one, down from the dearer).</summary>
    public PlanColumn? Other => Columns.Where(c => !c.Offer.IsFree && Plan is { } p && c.Offer.Id != p.Id).LastOrDefault();
    public bool CanSwitchPlan => CanManage && !IsEnding && Other is not null;
    public string SwitchText => Other is { } o ? Lang.T($"Switch to {o.Name}") : "";
    public string OtherText => Other is { } o ? Lang.T($"{o.Name} · {PagesText(o.Offer.Pages.Count)}") : "";
    public bool SwitchGoesUp => Other is { } o && Plan is { } p && Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is { } mine && o.Offer.PriceCents > mine.Offer.PriceCents;

    // ── The Plans screen ────────────────────────────────────────────────
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _comparing;
    public string CompareText => Comparing ? Lang.T("Hide the page by page list") : Lang.T("Compare plans page by page");
    partial void OnComparingChanged(bool value) => OnPropertyChanged(nameof(CompareText));

    public string Heading => IsFree ? Lang.T("Ask RigSight about anything on your PC") : Lang.T("Your RigSight AI plan");
    public string Subheading => IsFree ? Lang.T("Answers come from what RigSight recorded on this PC, and from the web.")
        : Lang.T("Switch, cancel or change your card here. Changes take effect straight away.");

    public IReadOnlyList<PlanCompareRow> CompareRows => Columns.Count < 3 ? []
        : [.. AiPages.All.Select(p => new PlanCompareRow(p.Label, p.Description, Columns[0].Offer.Pages.Contains(p.Id), Columns[1].Offer.Pages.Contains(p.Id), Columns[2].Offer.Pages.Contains(p.Id)))];

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
    [RelayCommand] private void Compare() => Comparing = !Comparing;

    /// <summary>Why the last action didn't work (shown where it was pressed), or null.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string? _problem;
    public bool HasProblem => Problem is not null;

    [ObservableProperty] private string? _done;

    // ── Buying ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _waiting;
    private CancellationTokenSource? _wait;
    private static readonly TimeSpan BuyWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Opens the plan's checkout (Lemon Squeezy, in the browser; the link carries this PC's id) and waits for the relay to
    /// say this PC is on it. True once it is; the Ask window then sends the question that hit the limit.
    /// </summary>
    public async Task<bool> BuyAsync(string planId)
    {
        Problem = null;
        if (_ask.Standing?.Plans is not { Count: > 0 } && await _ask.RefreshQuestionsAsync(theyAsked: true) is { } unreachable) { Problem = unreachable; return false; }
        var offer = _ask.Standing?.Plans.FirstOrDefault(p => p.Id == planId);
        if (offer is null || !Uri.TryCreate(offer.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            Log.Write("plans", $"Get {planId}: the relay has no checkout for it yet");
            Problem = Lang.T("Buying isn't open yet. Try again later.");
            return false;
        }
        Log.Write("plans", $"Opening the checkout for {planId}");
        if (!Browser.Open(offer.Url)) { Problem = Lang.T("Your browser couldn't be opened. Try again."); return false; }
        _wait?.Cancel();
        var cts = _wait = new CancellationTokenSource(BuyWait);
        Waiting = true;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                var (status, problem) = await Relay.StatusAsync(cts.Token);
                if (status is null) { Log.Write("plans", $"Waiting for {planId}: {problem}"); continue; }
                if (status.Plan?.Id != planId) continue;
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

    [RelayCommand] private async Task Buy(PlanColumn? column) { if (column is not null) await BuyAsync(column.Offer.Id); }
    [RelayCommand] private void StopWaiting() => _wait?.Cancel();

    // ── Cancel, keep, switch ────────────────────────────────────────────
    public IReadOnlyList<CancelReason> Reasons { get; }
    [ObservableProperty] private bool _cancelOpen;
    public string CancelTitle => Lang.T($"Cancel {PlanName}?");
    public string CancelText => Plan is { RenewsAt: { } on } ? Lang.T($"You won't be charged again. {PlanName} keeps working until {TimeText.At(on, "d MMMM")}, then you're back on 10 free questions a week.")
        : Lang.T($"You won't be charged again. {PlanName} keeps working until the month you paid for ends, then you're back on 10 free questions a week.");

    [RelayCommand] private void AskCancel() { Problem = null; foreach (var r in Reasons) r.Picked = false; CancelOpen = true; }
    [RelayCommand] private void Pick(CancelReason? reason) { foreach (var r in Reasons) r.Picked = r == reason && !r.Picked; }
    [RelayCommand]
    private void CloseDialogs()
    {
        CancelOpen = false;
        SwitchOpen = false;
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
        : Up(to) && p.RenewsAt is { } on
            ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this month now (about {Estimate(to)}), then {to.Offer.Price} a month from {TimeText.At(on, "d MMMM")}.")
        : Up(to)
            ? Lang.T($"{to.Name} opens straight away, with up to {to.Offer.Cap} questions a day. You pay the difference for the rest of this month now (about {Estimate(to)}), then {to.Offer.Price} a month.")
            : Lang.T($"It starts now: answers use {PagesText(to.Offer.Pages.Count)}, with up to {to.Offer.Cap} questions a day. What's left of this month comes off your next payment.");

    private bool Up(PlanColumn to) => Plan is { } p && Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is { } mine && to.Offer.PriceCents > mine.Offer.PriceCents;

    private string Estimate(PlanColumn to)
    {
        if (Plan is not { RenewsAt: { } renews } p || Columns.FirstOrDefault(c => c.Offer.Id == p.Id) is not { } mine) return to.Offer.Price;
        // The share of this billing month still to come, from its own length: over 30 days it said "about $5.20" for a $5
        // difference on the day the plan was bought (31 days left of a 31-day month, divided by 30; 2026-10-24).
        double month = (renews - renews.AddMonths(-1)).TotalDays;
        double left = Math.Clamp((renews - DateTime.Now).TotalDays / month, 0, 1);
        double dollars = (to.Offer.PriceCents - mine.Offer.PriceCents) / 100.0 * left;
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
