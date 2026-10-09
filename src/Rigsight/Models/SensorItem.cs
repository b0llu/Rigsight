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

    public string DisplayName => string.IsNullOrWhiteSpace(CustomLabel) ? Name : CustomLabel!;

    /// <summary>What the name says when pointed at: that it can be renamed, and what the part calls it once it has been.</summary>
    public string NameTip => string.IsNullOrWhiteSpace(CustomLabel) ? "Click to rename" : $"{Name}  ·  click to rename";

    /// <summary>Two-way bindable name used by the inline rename box. Empty resets to the original name.</summary>
    public string Label
    {
        get => DisplayName;
        set
        {
            var trimmed = value?.Trim();
            var newLabel = string.IsNullOrEmpty(trimmed) || trimmed == Name ? null : trimmed;
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
    /// <summary>Today's range: the agent tracks it all day; readings while the app is open widen it too.</summary>
    public string MinMaxText => Min is null ? "" : $"↓ {Units.Compact(Kind, Min)}   ↑ {Units.Compact(Kind, Max)}";

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
        Value = v;
        if (v is double d)
        {
            Min = Min is double mn ? Math.Min(mn, d) : d;
            Max = Max is double mx ? Math.Max(mx, d) : d;
            _sum += d;
            _count++;
            Average = _sum / _count;
        }

        OnPropertyChanged(nameof(FormattedValue));
        OnPropertyChanged(nameof(ShortValue));
        OnPropertyChanged(nameof(FormattedMin));
        OnPropertyChanged(nameof(FormattedMax));
        OnPropertyChanged(nameof(FormattedAverage));
        OnPropertyChanged(nameof(MinMaxText));
        Version++;
    }

    /// <summary>Today's lowest and highest reading as tracked by the agent (replaces the since-opened range).</summary>
    public void SetTodayRange(double min, double max)
    {
        Min = min;
        Max = max;
        OnPropertyChanged(nameof(FormattedMin));
        OnPropertyChanged(nameof(FormattedMax));
        OnPropertyChanged(nameof(MinMaxText));
    }

    /// <summary>Re-evaluates every formatted string (e.g. after switching °C/°F).</summary>
    public void RefreshFormatting() => OnPropertyChanged(string.Empty);
}
