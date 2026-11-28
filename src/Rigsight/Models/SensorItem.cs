using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Models;

/// <summary>UI-facing view of one hardware sensor, with statistics and history.</summary>
public sealed partial class SensorItem : ObservableObject
{
    // One hour at a 1 s refresh rate.
    private const int HistoryCapacity = 3600;

    private double _sum;
    private long _count;

    public SensorItem(SensorMeta meta, string hardwareName, string hardwareType)
    {
        Id = meta.Id;
        Name = meta.Name;
        Kind = meta.Kind;
        HardwareName = hardwareName;
        HardwareType = hardwareType;
        TempScale = Kind == SensorKind.Temperature ? ScaleFor(hardwareType, Name) : null;
    }

    public string Id { get; }
    public string Name { get; }
    public SensorKind Kind { get; }
    public string HardwareName { get; }

    /// <summary>The part's name as shown: in the language in use where the hardware library names it generically ("Total Memory").</summary>
    public string HardwareShown => Core.SensorWords.Show(HardwareName);
    public string HardwareType { get; }
    public HistoryBuffer History { get; } = new(HistoryCapacity);

    /// <summary>
    /// Where this temperature turns from cool to good to warm to hot ("45,70,85"), the same steps its part has on
    /// Temperatures and Storage. None for a limit the part reports (a drive's "Critical Temperature"), which never
    /// moves, and for parts with no known steps: those are shown in the plain text colour.
    /// </summary>
    public string? TempScale { get; }
    public bool HasTempScale => TempScale is not null;

    private static readonly string[] FixedTemperatures = ["Warning", "Critical", "Limit", "Threshold", "Distance", "TjMax", "Shutdown"];

    private static string? ScaleFor(string hardwareType, string name)
    {
        if (FixedTemperatures.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) return null;
        return hardwareType switch
        {
            "Cpu" => "45,70,85",
            "GpuNvidia" or "GpuAmd" or "GpuIntel" =>
                name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ? "50,80,95"
                : name.Contains("Junction", StringComparison.OrdinalIgnoreCase) ? "55,85,100"
                : "45,70,83",
            "Storage" => "35,50,65",
            "Motherboard" or "SuperIO" or "EmbeddedController" => "40,60,80",
            _ => null,
        };
    }

    /// <summary>Raised when the user renames or hides/unhides the sensor, so settings can be saved.</summary>
    public event Action<SensorItem>? UserPreferenceChanged;

    [ObservableProperty] private double? _value;
    [ObservableProperty] private double? _min;
    [ObservableProperty] private double? _max;
    [ObservableProperty] private double? _average;

    /// <summary>Incremented on every sample so charts bound to it re-render.</summary>
    [ObservableProperty] private long _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(Label), nameof(NameTip))]
    private string? _customLabel;

    [ObservableProperty] private bool _isHidden;

    /// <summary>The one just jumped to from the pane beside the All sensors list: its row is lit until another is.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Whether the row passes the current filter on the Sensors page.</summary>
    [ObservableProperty] private bool _isShown = true;

    /// <summary>The name a person gave it, else its own name in the language in use (see <see cref="SensorWords"/>).</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomLabel) ? CustomLabel!
        // A device's battery is named by its device wherever it's picked or listed ("G309 LIGHTSPEED battery").
        : Kind == SensorKind.Battery ? Lang.T($"{HardwareShown} battery") : SensorWords.Show(Name);

    /// <summary>For a wireless device's battery: how the device is reached ("Bluetooth"), from its group.</summary>
    public string? Via { get; init; }

    /// <summary>For a wireless device's battery: what the device is ("Mouse"...), for its icon on the taskbar.</summary>
    public string? Device { get; init; }

    /// <summary>
    /// For a wireless device's battery: what the device says about charging (1.22.0), from the agent's tick. Unknown where
    /// it can't say (Bluetooth, Xbox controllers): nothing is shown then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBolt), nameof(ShowsFull), nameof(ChargeTip))]
    private ChargeState _charge;

    /// <summary>A bolt after the number: the device says it's charging (see <see cref="DeviceCharge.MarkOf"/>).</summary>
    public bool ShowsBolt => DeviceCharge.MarkOf(Charge, Value) == DeviceCharge.Mark.Bolt;

    /// <summary>"Full" in grey after the number: the device says it's full on power, at 100%.</summary>
    public bool ShowsFull => DeviceCharge.MarkOf(Charge, Value) == DeviceCharge.Mark.Full;

    /// <summary>What the number's hover says while charging or full ("Charging, 45% now"); null otherwise.</summary>
    public string? ChargeTip => DeviceCharge.Tip(Charge, Value);

    /// <summary>The word after a full device's number, as the battery's own levels say it.</summary>
    public static string FullWord => Lang.C("battery", "Full");

    /// <summary>What the name says when pointed at: that it can be renamed, and what the part calls it once it has been.</summary>
    public string NameTip => Lang.T($"{(string.IsNullOrWhiteSpace(CustomLabel) ? DisplayName : SensorWords.Show(Name))}  ·  click to rename");

    /// <summary>Two-way bindable name used by the inline rename box. Empty resets to the original name.</summary>
    public string Label
    {
        // A device's battery sits under its device's own heading on All sensors, so its row says "Battery" alone there.
        get => Kind == SensorKind.Battery && string.IsNullOrWhiteSpace(CustomLabel) ? SensorWords.Show(Name) : DisplayName;
        set
        {
            var trimmed = value?.Trim();
            // Its own name back, in English or as shown, is no rename.
            var newLabel = string.IsNullOrEmpty(trimmed) || trimmed == Name || trimmed == SensorWords.Show(Name) ? null : trimmed;
            if (newLabel == CustomLabel) return;
            CustomLabel = newLabel;
            UserPreferenceChanged?.Invoke(this);
        }
    }

    public string TypeLabel => Units.TypeLabel(Kind);
    public string FormattedValue => Units.Format(Kind, Value);
    public string FormattedMin => Units.Format(Kind, Min);
    public string FormattedMax => Units.Format(Kind, Max);
    public string FormattedAverage => Units.Format(Kind, Average);
    public string ShortValue => Units.Short(Kind, Value);
    /// <summary>
    /// Today's range: the agent tracks it all day; readings while the app is open widen it too. In words ("37° to 55°
    /// today"), as every range is said, not arrows.
    /// </summary>
    public string MinMaxText => RangeText;

    /// <summary>A device battery card's small grey line (1.22.0): "Lowest 35% · Highest 65% · Average 35%".</summary>
    public string BatteryStats => Min is double lo && Max is double hi && Average is double avg && Units.BatteryWord(lo) is null
        ? Lang.T($"Lowest {Units.Short(Kind, lo)} · Highest {Units.Short(Kind, hi)} · Average {Units.Short(Kind, avg)}") : "";

    /// <summary>Today's range in words under a tile ("36° to 58° today"); empty before there is one.</summary>
    public string RangeText => Min is double lo && Max is double hi ? Lang.T($"{Units.Compact(Kind, lo)} to {Units.Compact(Kind, hi)} today") : "";

    /// <summary>Where the reading sits in today's range, 0 (its lowest) to 100 (its highest); the middle when the range has no width yet.</summary>
    public double RangeShare => Value is double v && Min is double lo && Max is double hi
        ? hi - lo < 0.5 ? 50 : Math.Clamp((v - lo) / (hi - lo) * 100, 0, 100)
        : 0;

    public void ToggleHidden()
    {
        IsHidden = !IsHidden;
        UserPreferenceChanged?.Invoke(this);
    }

    /// <summary>Fills history from the agent's buffer when the app connects (doesn't affect min/max).</summary>
    public void Seed(long[] times, float?[] values)
    {
        for (int i = 0; i < times.Length && i < values.Length; i++)
            AddSample(times[i], values[i] is float f ? f : double.NaN);
    }

    /// <summary>
    /// Readings come every second or two (drives every few minutes); a longer silence means the PC slept or
    /// the agent wasn't running. A break is recorded there, so charts show a gap instead of a straight line.
    /// </summary>
    private long GapMs => HardwareType == "Storage" ? 7 * 60_000 : 15_000;

    private void AddSample(long timeMs, double value)
    {
        long last = History.LastTime;
        if (History.Count > 0 && timeMs - last > GapMs) History.Add(last + 1, double.NaN);
        History.Add(timeMs, value);
    }

    public void Push(long timeMs, float? raw)
    {
        double? v = raw is float f && float.IsFinite(f) ? f : null;
        if (Kind == SensorKind.Temperature && v <= 0) v = null;

        AddSample(timeMs, v ?? double.NaN);
        bool moved = Value != v;
        Value = v;
        if (v is double d)
        {
            Min = Min is double mn ? Math.Min(mn, d) : d;
            Max = Max is double mx ? Math.Max(mx, d) : d;
            _sum += d;
            _count++;
            Average = _sum / _count;
        }

        // Only the words that read differently are said to have changed (1.23.0): a steady reading's row on All sensors
        // was told of nine changes at every reading, and redid every binding of them each time.
        Said(ref _saidMin, FormattedMin, nameof(FormattedMin));
        Said(ref _saidMax, FormattedMax, nameof(FormattedMax));
        Said(ref _saidValue, FormattedValue, nameof(FormattedValue));
        // Pages that work things out from the short figure (a drive's health) hear of every new reading, as before.
        if (!Said(ref _saidShort, ShortValue, nameof(ShortValue)) && moved) OnPropertyChanged(nameof(ShortValue));
        Said(ref _saidAverage, FormattedAverage, nameof(FormattedAverage));
        if (Said(ref _saidRange, RangeText, nameof(RangeText))) OnPropertyChanged(nameof(MinMaxText));
        if (Kind == SensorKind.Battery) OnPropertyChanged(nameof(BatteryStats));
        if (RangeShare is var share && share != _saidShare)
        {
            _saidShare = share;
            OnPropertyChanged(nameof(RangeShare));
        }
        if (Kind == SensorKind.Battery) OnPropertyChanged(nameof(BatteryTrend));
        Version++;
    }

    // The words last said for each figure (null: not said yet, or to be said again after RefreshFormatting).
    private string? _saidValue, _saidShort, _saidMin, _saidMax, _saidAverage, _saidRange;
    private double _saidShare = double.NaN;

    /// <summary>Says <paramref name="name"/> changed when it now reads differently from <paramref name="said"/>; true if it did.</summary>
    private bool Said(ref string? said, string now, string name)
    {
        if (said == now) return false;
        said = now;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Every figure is said again at the next reading (the unit changed, or the range was set from outside).</summary>
    private void Unsaid()
    {
        _saidValue = _saidShort = _saidMin = _saidMax = _saidAverage = _saidRange = null;
        _saidShare = double.NaN;
    }

    /// <summary>For a wireless device's battery: what it did in the last 10 minutes ("Steady"), see <see cref="DeviceCharge.Trend"/>.</summary>
    public string? BatteryTrend => Kind == SensorKind.Battery ? DeviceCharge.Trend(Readings(), History.LastTime) : null;

    private IEnumerable<(long, double)> Readings()
    {
        for (int i = History.IndexAtOrAfter(History.LastTime - DeviceCharge.TrendMinutes * 60_000L); i < History.Count; i++)
            yield return (History.TimeAt(i), History.ValueAt(i));
    }

    /// <summary>Today's lowest and highest reading as tracked by the agent (replaces the since-opened range).</summary>
    public void SetTodayRange(double min, double max)
    {
        Min = min;
        Max = max;
        Unsaid();
        OnPropertyChanged(nameof(FormattedMin));
        OnPropertyChanged(nameof(FormattedMax));
        OnPropertyChanged(nameof(MinMaxText));
        if (Kind == SensorKind.Battery) OnPropertyChanged(nameof(BatteryStats));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(RangeShare));
    }

    /// <summary>A charging device's mark follows its number too (a bolt under 100%, "Full" at it).</summary>
    partial void OnValueChanged(double? value)
    {
        if (Charge == ChargeState.Unknown) return;
        OnPropertyChanged(nameof(ShowsBolt));
        OnPropertyChanged(nameof(ShowsFull));
        OnPropertyChanged(nameof(ChargeTip));
    }

    /// <summary>Re-evaluates every formatted string (e.g. after switching °C/°F).</summary>
    public void RefreshFormatting()
    {
        Unsaid();
        OnPropertyChanged(string.Empty);
    }
}
