using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Export;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>The Power page's facts for a period, with the names and paths of the apps it mentions.</summary>
public sealed record PowerPage(PowerReport Report, IReadOnlyDictionary<long, string> Names, IReadOnlyDictionary<long, string?> Paths);

/// <summary>A label and its value in "How the CPU and GPU ran".</summary>
public sealed record PowerFact(string Label, string Value);

/// <summary>An app in "What was in front": its time in front and the CPU and GPU energy in that time, with a bar against the first.</summary>
public sealed record PowerAppRow(string Name, string? Path, string Time, string Energy, double Share, string Tip);

/// <summary>A currency in the price box: "₹ INR" closed (<see cref="Short"/>), "₹ INR, Indian Rupee" in its list (<see cref="Text"/>).</summary>
public sealed record CurrencyChoice(string Code, string Text, string Short);

/// <summary>
/// The Power page (1.18.0, approved mock-up 2026-11-23, issue #12 asks 2 and 3 and the reporter's follow-up 2): where the
/// CPU's and GPU's power went over a period and how that changed. The headline is the CPU + GPU energy against the period
/// before with what changed in play time, then the chart, how the CPU and GPU ran, and the apps in front, with an optional
/// electricity price for an estimated cost. Never "PC energy": the rest of the PC isn't measured, and never an app's own
/// watts: the energy beside an app is the PC's in the time it was in front.
/// </summary>
public sealed partial class PowerViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly SettingsModel _settings;

    public LiveData Live { get; }

    public PowerViewModel(ReportService reports, LiveData live, SettingsModel settings)
    {
        _reports = reports;
        _settings = settings;
        Live = live;
        settings.Changed += () => { OnPropertyChanged(nameof(HasPrice)); RefreshCost(); };
    }

    // ── The period (rolling, as the other pages': the last 7 days, the last 30 days, the last 12 months) ──

    /// <summary>Opens on the last 7 days: a day's energy alone says little (the approved mock-up).</summary>
    [ObservableProperty] private ReportRange _unit = ReportRange.Week;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _firstDay;
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);
    /// <summary>Drawn by the hour as lines: a day, or a custom range of 2 days or less.</summary>
    public bool IsDay => PowerReport.StepOf(PeriodOf(Unit), Period.From, Period.To) == PowerStep.Hour;
    public bool IncludesToday => Period.To > DateTime.Today;
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, CustomFrom, CustomTo);

    private static PowerPeriod PeriodOf(ReportRange unit) => unit switch
    {
        ReportRange.Day => PowerPeriod.Day, ReportRange.Month => PowerPeriod.Month, ReportRange.Year => PowerPeriod.Year,
        ReportRange.Custom => PowerPeriod.Custom, _ => PowerPeriod.Week,
    };

    partial void OnUnitChanged(ReportRange value) { OnPropertyChanged(nameof(IsDay)); OnPropertyChanged(nameof(RangeNote)); if (!_quiet && (value != ReportRange.Custom || CustomTo > CustomFrom)) _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { OnPropertyChanged(nameof(RangeNote)); if (!_quiet) _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    // A custom range (2026-11-24: every page that looks back has one): read once both ends are set.
    private void CustomChanged()
    {
        OnPropertyChanged(nameof(IsDay));
        OnPropertyChanged(nameof(RangeNote));
        if (!_quiet && Unit == ReportRange.Custom && CustomTo > CustomFrom) _ = RefreshAsync();
    }

    /// <summary>Shows a period picked elsewhere (Reports' Energy tile), a custom range as it was.</summary>
    public void ShowPeriod(ReportRange unit, DateTime anchor, DateTime customFrom = default, DateTime customTo = default)
    {
        if (unit == ReportRange.Custom)
        {
            if (customTo <= customFrom) { (Unit, Anchor) = (ReportRange.Week, anchor); return; }
            _quiet = true;
            (CustomFrom, CustomTo) = (customFrom, customTo);
            _quiet = false;
        }
        (Unit, Anchor) = (unit, anchor);
        if (unit == ReportRange.Custom) _ = RefreshAsync();
    }

    public void NewDay(DateTime was)
    {
        var anchor = PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        _ = RefreshAsync();
    }

    [ObservableProperty] private bool _isLoading;
    /// <summary>Read at least once: until then only the loader shows.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowEmpty), nameof(ShowPage))] private bool _loaded;

    private PowerPage? _page;
    private int _load;

    public async Task RefreshAsync()
    {
        int id = ++_load;
        IsLoading = true;
        try
        {
            FirstDay ??= await _reports.FirstMinuteDayAsync();
            var period = PeriodOf(Unit);
            var (from, to) = Period;
            if (to <= from) return;
            var page = await _reports.PowerAsync(period, from, to);
            if (id != _load) return;
            _page = page;
            Fill();
            Loaded = true;
        }
        finally
        {
            if (id == _load) IsLoading = false;
        }
    }

    // ── What the page shows ──

    public bool ShowLoading => !Loaded;
    /// <summary>Nothing recorded in the period, or no part reporting its power: one plain line in place of the page.</summary>
    public bool ShowEmpty => Loaded && EmptyText.Length > 0;
    public bool ShowPage => Loaded && EmptyText.Length == 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowPage))] private string _emptyText = "";
    [ObservableProperty] private string _emptyNote = "";

    [ObservableProperty] private string _headCaption = "";
    [ObservableProperty] private string _headNumber = "";
    [ObservableProperty] private string _perDay = "";
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private bool _hasBothParts;
    [ObservableProperty] private string _notWhole = "";
    [ObservableProperty] private string _changeText = "";
    [ObservableProperty] private string _gameLine = "";
    [ObservableProperty] private bool _gameLineWarm;
    [ObservableProperty] private string _batteryLine = "";
    [ObservableProperty] private double _gamesShare;
    [ObservableProperty] private string _gamesText = "";
    [ObservableProperty] private string _otherText = "";
    [ObservableProperty] private bool _hasGames;

    [ObservableProperty] private IReadOnlyList<PowerSlot> _slots = [];
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private bool _chartHasCpu;
    [ObservableProperty] private bool _chartHasGpu;

    [ObservableProperty] private IReadOnlyList<PowerFact> _cpuFacts = [];
    [ObservableProperty] private IReadOnlyList<PowerFact> _gpuFacts = [];
    [ObservableProperty] private string _cpuMissing = "";
    [ObservableProperty] private string _gpuMissing = "";

    [ObservableProperty] private IReadOnlyList<PowerAppRow> _apps = [];
    [ObservableProperty] private string _appsNote = "";
    public bool HasApps => Apps.Count > 0;
    partial void OnAppsChanged(IReadOnlyList<PowerAppRow> value) => OnPropertyChanged(nameof(HasApps));

    private string Missing(bool cpu) => cpu
        ? Live.StandardAccount ? Lang.T("CPU power needs admin rights on this PC.") : Lang.T("Your CPU doesn't report its power.")
        : Lang.T("Your GPU doesn't report its power.");

    private void Fill()
    {
        var page = _page;
        if (page is null) { EmptyText = Wording.NotRecorded; EmptyNote = ""; return; }
        var r = page.Report;
        string Name(long id) => page.Names.TryGetValue(id, out var n) ? n : "";
        if (r.Buckets.Count == 0)
        {
            EmptyText = IncludesToday ? Lang.T("Nothing measured yet") : Wording.NotRecorded;
            EmptyNote = "";
            return;
        }
        if (!r.HasAny)
        {
            EmptyText = Lang.T("Your CPU and GPU don't report their power on this PC.");
            EmptyNote = Lang.T("Their load, clocks and temperatures are still on Temperatures, and each app's CPU time on Processes.");
            return;
        }
        EmptyText = "";

        // The headline: the energy of the parts that report it, named as what it is.
        HeadCaption = r.HasCpu && r.HasGpu ? Lang.T("CPU + GPU ENERGY") : r.HasCpu ? Lang.T("CPU ENERGY") : Lang.T("GPU ENERGY");
        HeadNumber = PowerReport.KwhNumber(r.Wh);
        PerDay = r.Days > 1 ? Lang.T($"About {PowerReport.Kwh(r.Wh / r.Days)} a day") : "";
        HasBothParts = r.HasCpu && r.HasGpu;
        CpuText = r.CpuWh is double c ? Lang.T($"CPU {PowerReport.Kwh(c)}") : "";
        GpuText = r.GpuWh is double g ? Lang.T($"GPU {PowerReport.Kwh(g)}") : "";
        NotWhole = !r.HasGpu ? Lang.T("Your GPU doesn't report its power, so only the CPU is counted.")
            : !r.HasCpu ? (Live.StandardAccount ? Lang.T("CPU power needs admin rights on this PC, so only the GPU is counted.") : Lang.T("Your CPU doesn't report its power, so only the GPU is counted."))
            : Lang.T("The rest of the PC and the power supply aren't measured.");
        ChangeText = string.Join(" ", new[] { r.Change, r.Reason }.Where(s => s.Length > 0));
        GameLine = r.GameLine ?? "";
        GameLineWarm = r.GameLineWarm;
        BatteryLine = r.Parts.BatteryMinutes > 0 ? Lang.T($"On battery for {Units.Duration(r.Parts.BatteryMinutes * 60.0)} of it.") : "";
        HasGames = r.Parts.GameMinutes > 0 && r.Wh > 0;
        GamesShare = r.GamesPercent / 100.0;
        GamesText = Lang.T($"Games {r.GamesPercent}%");
        OtherText = Lang.T($"Everything else {100 - r.GamesPercent}%");
        RefreshCost();

        // The chart.
        ChartHasCpu = r.HasCpu;
        ChartHasGpu = r.HasGpu;
        ChartTitle = r.Step switch { PowerStep.Hour => r.Period == PowerPeriod.Day ? Lang.T("Power through the day") : Lang.T("Power each hour"), PowerStep.Month => Lang.T("Energy each month"), _ => Lang.T("Energy each day") };
        Slots = SlotsOf(r, Name);

        // How each part ran.
        CpuMissing = r.HasCpu ? "" : Missing(cpu: true);
        GpuMissing = r.HasGpu ? "" : Missing(cpu: false);
        var p = r.Parts;
        CpuFacts = r.HasCpu ?
        [
            new(Lang.T("Average"), PowerReport.Watts(p.Cpu.Average)),
            new(Lang.T("In games"), PowerReport.Watts(p.Cpu.InGames)),
            new(Lang.T("At low activity"), PowerReport.Watts(p.Cpu.AtRest)),
            new(Lang.T("Highest minute"), PowerReport.Watts(p.Cpu.Highest)),
            new(Lang.T("Average clock"), PowerReport.Clock(p.Cpu.Clock)),
            new(Lang.T("Time at heavy load"), PowerReport.Heavy(p.Cpu.HeavyMinutes)),
        ] : [];
        GpuFacts = r.HasGpu ?
        [
            new(Lang.T("Average"), PowerReport.Watts(p.Gpu.Average)),
            new(Lang.T("In games"), PowerReport.Watts(p.Gpu.InGames)),
            new(Lang.T("At low activity"), PowerReport.Watts(p.Gpu.AtRest)),
            new(Lang.T("Highest minute"), PowerReport.Watts(p.Gpu.Highest)),
            new(Lang.T("Clock in games"), PowerReport.Clock(p.Gpu.Clock)),
            new(Lang.T("Time at heavy load"), PowerReport.Heavy(p.Gpu.HeavyMinutes)),
        ] : [];

        // The apps in front, the most energy first.
        double top = r.Apps.Count > 0 ? Math.Max(1, r.Apps[0].Wh) : 1;
        Apps = [.. r.Apps.Where(a => a.Wh >= 5 && Name(a.App).Length > 0).Take(30).Select(a =>
        {
            string time = Units.Duration(a.Minutes * 60.0);
            string energy = PowerReport.Kwh(a.Wh);
            return new PowerAppRow(Name(a.App), page.Paths.GetValueOrDefault(a.App), a.IsGame ? Lang.T($"{time} played") : Lang.T($"{time} in front"), energy,
                Math.Clamp(a.Wh / top, 0.02, 1),
                Lang.T($"The CPU and GPU used {energy} in the {time} {Name(a.App)} was in front. Anything else running at the same time counts too."));
        })];
        AppsNote = r.HasCpu && r.HasGpu ? Lang.T("The CPU + GPU energy in the time each app was in front, not what the app itself drew.")
            : r.HasCpu ? Lang.T("The CPU energy in the time each app was in front, not what the app itself drew.")
            : Lang.T("The GPU energy in the time each app was in front, not what the app itself drew.");
    }

    /// <summary>Every slot of the period (hours of a day, days, months), the ones not recorded empty, with its hover lines.</summary>
    private List<PowerSlot> SlotsOf(PowerReport r, Func<long, string> name)
    {
        var slots = new List<PowerSlot>();
        var byStart = r.Buckets.ToDictionary(b => TimeUtil.FromUnix(b.Start));
        var price = Price();
        var paths = _page?.Paths ?? new Dictionary<long, string?>();
        var games = r.Apps.Where(a => a.IsGame).Select(a => a.App).ToHashSet();
        if (r.Step == PowerStep.Day) IconCache.Warm(r.Buckets.SelectMany(b => b.Apps ?? []).Select(a => paths.GetValueOrDefault(a.App)).Distinct());
        IEnumerable<DateTime> starts = r.Step switch
        {
            PowerStep.Hour => Enumerable.Range(0, (int)Math.Ceiling((r.To - r.From).TotalHours)).Select(h => r.From.AddHours(h)),
            PowerStep.Month => Months(r.From, r.To),
            _ => Enumerable.Range(0, (int)Math.Ceiling((r.To - r.From.Date).TotalDays)).Select(d => r.From.Date.AddDays(d)),
        };

        // A year's months, or a long custom range's (from the month it starts in).
        static IEnumerable<DateTime> Months(DateTime from, DateTime to)
        {
            for (var m = new DateTime(from.Year, from.Month, 1); m < to; m = m.AddMonths(1)) yield return m;
        }
        var list = starts.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var at = list[i];
            byStart.TryGetValue(at, out var b);
            // About eight labels however long it is (a custom range can be any length).
            int every = Math.Max(1, (int)Math.Ceiling(list.Count / 8.0));
            string axis = (r.Step, r.Period) switch
            {
                (PowerStep.Hour, PowerPeriod.Day) => i % 3 == 0 ? TimeText.At(at, "h tt") : "",
                (PowerStep.Hour, _) => i % every == 0 ? (at.Hour == 0 || i == 0 ? at.ToString("ddd", CultureInfo.CurrentCulture) + " " : "") + TimeText.At(at, "h tt") : "",
                (PowerStep.Day, PowerPeriod.Week) => at.ToString("ddd d", CultureInfo.CurrentCulture),
                (PowerStep.Day, PowerPeriod.Month) => i % 5 == 0 ? at.ToString("d MMM", CultureInfo.CurrentCulture) : "",
                (PowerStep.Day, _) => i % every == 0 ? at.ToString("d MMM", CultureInfo.CurrentCulture) : "",
                _ => list.Count <= 12 || i % every == 0 ? at.ToString("MMM", CultureInfo.CurrentCulture) : "",
            };
            string title = r.Step switch
            {
                PowerStep.Hour => r.Period == PowerPeriod.Day ? TimeText.At(at, "h tt") : TimeText.At(at, "ddd d MMM, h tt"),
                PowerStep.Month => TimeText.At(at, "MMMM yyyy"),
                _ => at.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            };
            var lines = new List<string>();
            double? cpu = null, gpu = null;
            if (b is null || b.Minutes == 0) lines.Add(Wording.NotRecorded);
            else if (r.Step == PowerStep.Hour)
            {
                // An hour: its average watts (the energy over the minutes recorded, as watts).
                cpu = b.CpuWh is double c ? c * 60 / b.Minutes : null;
                gpu = b.GpuWh is double g ? g * 60 / b.Minutes : null;
                if (cpu is double cw) lines.Add(Lang.T($"CPU average {PowerReport.Watts(cw)}"));
                if (gpu is double gw) lines.Add(Lang.T($"GPU average {PowerReport.Watts(gw)}"));
                lines.Add(b.TopApp is long app && name(app).Length > 0 ? Lang.T($"{name(app)} in front") : Lang.T("Nothing in front"));
            }
            else
            {
                cpu = b.CpuWh is double c ? c / 1000 : null;
                gpu = b.GpuWh is double g ? g / 1000 : null;
                if (b.CpuWh is double cw) lines.Add(Lang.T($"CPU {PowerReport.Kwh(cw)}"));
                if (b.GpuWh is double gw) lines.Add(Lang.T($"GPU {PowerReport.Kwh(gw)}"));
                if (b.CpuWh is not null && b.GpuWh is not null) lines.Add(Lang.T($"{PowerReport.Kwh(b.Wh)} together"));
                if (price is var (perKwh, code)) lines.Add(Lang.T($"About {Money.Format(b.Wh / 1000 * perKwh, code)}"));
                if (b.TopApp is long app && name(app).Length > 0) lines.Add(Lang.T($"Most of it with {name(app)} in front"));
            }
            // A day's own hover (1.22.0): its parts, its usual weekday, its cost and the apps in front with their icons.
            Func<RichTip>? rich = r.Step == PowerStep.Day && b is { Minutes: > 0 } day ? () => DayTip(r, day, at, name, paths, games, Price()) : null;
            slots.Add(new PowerSlot(axis, title, cpu, gpu, lines, rich));
        }
        return slots;
    }

    /// <summary>
    /// A day's hover (the approved mock-up's Energy each day): the day and its time in games, its energy big, the CPU and
    /// the GPU in their colours with their share of the day, against the usual for its weekday, its estimated cost, and
    /// the apps in front with their own icons. No click line: a click on a day opens nothing.
    /// </summary>
    private static RichTip DayTip(PowerReport r, PowerBucket b, DateTime day, Func<long, string> name, IReadOnlyDictionary<long, string?> paths,
        IReadOnlySet<long> games, (double PerKwh, string Code)? price)
    {
        var f = PowerDayHover.Of(b, day, r.DayUsualWh.TryGetValue(day.Date, out double usual) ? usual : null, DateTime.Now, games, price);
        Brush cpu = ChartPaint.CpuBrush, gpu = ChartPaint.Brush(ChartPaint.Gpu);
        var split = new List<(Brush, double)>();
        var rows = new List<RichRow>();
        if (f.Cpu is { } c) { split.Add((cpu, c.Wh)); rows.Add(new RichRow(cpu, "CPU", PowerReport.Kwh(c.Wh), Lang.T($"{c.Percent}% of the day"))); }
        if (f.Gpu is { } g) { split.Add((gpu, g.Wh)); rows.Add(new RichRow(gpu, "GPU", PowerReport.Kwh(g.Wh), Lang.T($"{g.Percent}% of the day"))); }
        var facts = new List<RichFact>();
        if (f.AgainstLabel is { } label && f.Against is { } against) facts.Add(new RichFact(label, against));
        if (f.Cost is { } cost) facts.Add(new RichFact(Lang.T("Estimated cost"), cost));
        var apps = f.Apps.Where(a => name(a.App).Length > 0).Select(a =>
        {
            string n = name(a.App);
            var icon = IconCache.Get(paths.GetValueOrDefault(a.App)) is { } img ? new RichIcon(Image: img) : new RichIcon(Letter: n[..1].ToUpperInvariant());
            return new RichItem(icon, n, PowerReport.Kwh(a.Wh));
        }).ToList();
        return new RichTip(f.Title)
        {
            Sub = f.Sub,
            Big = f.Big,
            BigWord = f.BigWord,
            Split = split.Count > 1 ? split : [],
            Rows = rows,
            Facts = facts,
            // The day's own energy in each app's time in front, not what the app drew: said in the heading, as the Apps list's note says it.
            Notes = apps.Count > 0 ? [new RichNote(Lang.T("While each was in front"), apps)] : [],
        };
    }

    // ── The electricity price (one price a kWh, for every hour and every day shown) ──

    private (double PerKwh, string Code)? Price() =>
        _settings.Current.Power is { PricePerKwh: double p and > 0 } s ? (p, s.Currency ?? Money.HomeCurrency()) : null;

    public bool HasPrice => Price() is not null;

    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private string _priceText = "";

    private void RefreshCost()
    {
        OnPropertyChanged(nameof(HasPrice));
        if (Price() is not var (perKwh, code) || _page is null) { CostText = ""; PriceText = ""; return; }
        CostText = Lang.T($"Estimated cost {Money.Format(_page.Report.Wh / 1000 * perKwh, code)}");
        PriceText = Lang.T($"{Money.Format(perKwh, code)} per kWh");
        if (_page.Report.Buckets.Count > 0) Slots = SlotsOf(_page.Report, id => _page.Names.TryGetValue(id, out var n) ? n : "");
    }

    /// <summary>The price box is open (from "Add your electricity price" or Change).</summary>
    [ObservableProperty] private bool _priceOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSavePrice))] private string _priceInput = "";
    [ObservableProperty] private CurrencyChoice? _currency;

    private IReadOnlyList<CurrencyChoice>? _currencies;

    /// <summary>Every currency, the home location's first (India: the rupee), then by code.</summary>
    public IReadOnlyList<CurrencyChoice> Currencies => _currencies ??= MakeCurrencies();

    private static List<CurrencyChoice> MakeCurrencies()
    {
        string home = Money.HomeCurrency();
        return [.. Money.Currencies().OrderBy(c => c.Code == home ? 0 : 1).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new CurrencyChoice(c.Code, $"{Money.Short(c.Code)}, {c.Name}", Money.Short(c.Code)))];
    }

    /// <summary>A price that reads as a positive number in the number format in use (or with a point).</summary>
    public bool CanSavePrice => ParsePrice(PriceInput) is not null;

    internal static double? ParsePrice(string text)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out double v) && v > 0 && v < 100_000) return v;
        if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v > 0 && v < 100_000) return v;
        return null;
    }

    [RelayCommand]
    private void OpenPrice()
    {
        var s = _settings.Current.Power;
        PriceInput = s.PricePerKwh is double p ? p.ToString("0.####", CultureInfo.CurrentCulture) : "";
        string code = s.Currency ?? Money.HomeCurrency();
        Currency = Currencies.FirstOrDefault(c => c.Code == code) ?? Currencies.FirstOrDefault();
        PriceOpen = true;
    }

    [RelayCommand] private void CancelPrice() => PriceOpen = false;

    [RelayCommand]
    private void SavePrice()
    {
        if (ParsePrice(PriceInput) is not double price) return;
        string code = Currency?.Code ?? Money.HomeCurrency();
        _settings.Update(s => { s.Power.PricePerKwh = price; s.Power.Currency = code; });
        Log.Write("power", $"Electricity price set to {price} {code} a kWh");
        PriceOpen = false;
        RefreshCost();
    }

    /// <summary>Remove price: only a setting, so it asks nothing; the page goes back to "Add your electricity price".</summary>
    [RelayCommand]
    private void RemovePrice()
    {
        _settings.Update(s => { s.Power.PricePerKwh = null; s.Power.Currency = null; });
        Log.Write("power", "Electricity price removed");
        PriceOpen = false;
        RefreshCost();
    }

    // ── Export ──

    /// <summary>The summary for a period other than the one on screen, worked out by a page of its own that's never shown.</summary>
    internal static async Task<List<FactRow>> SummaryAsync(ReportService reports, LiveData live, SettingsModel settings, ReportRange unit, DateTime anchor)
    {
        var page = new PowerViewModel(reports, live, settings) { _quiet = true };
        (page.Unit, page.Anchor) = (unit, anchor);
        page._quiet = false;
        await page.RefreshAsync();
        return page.SummaryFacts();
    }

    // Set while a page that's never shown is given its period (see SummaryAsync): one read, not one per step.
    private bool _quiet;

    /// <summary>The page's figures as facts (the Export box's summary).</summary>
    internal List<FactRow> SummaryFacts()
    {
        if (_page?.Report is not { HasAny: true } r) return [new(Lang.T("Energy"), Wording.NotRecorded, "")];
        var facts = new List<FactRow> { new(HeadCaption, PowerReport.Kwh(r.Wh), ChangeText) };
        if (r.CpuWh is double c) facts.Add(new(Lang.T("CPU"), PowerReport.Kwh(c), ""));
        if (r.GpuWh is double g) facts.Add(new(Lang.T("GPU"), PowerReport.Kwh(g), ""));
        if (HasGames) facts.Add(new(Lang.T("Games"), $"{r.GamesPercent}%", ""));
        if (CostText.Length > 0) facts.Add(new(Lang.T("Estimated cost"), CostText, PriceText));
        foreach (var a in Apps.Take(10)) facts.Add(new(a.Name, a.Energy, a.Time));
        return facts;
    }
}
