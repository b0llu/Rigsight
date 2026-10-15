using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>An app on a line of the box beside the lanes: its name and how much ("62%", "4.8 GB").</summary>
public sealed record CaseApp(string Name, string Value);

/// <summary>
/// One minute before a crash, everything recorded about the PC in it, for the lanes and the box beside them. Null
/// where it wasn't recorded (a standard account has no temperatures; minutes from before 0.24.0 have no drives,
/// memory promised, battery or apps' shares).
/// </summary>
public sealed class CaseMinute
{
    public required DateTime Time { get; init; }
    public double? CpuTemp { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotSpot { get; init; }
    public double? CpuLoad { get; init; }
    public double? GpuLoad { get; init; }
    public double? CpuPower { get; init; }
    public double? GpuPower { get; init; }
    /// <summary>Memory in use, GB.</summary>
    public double? RamGb { get; init; }
    public double? CommitPct { get; init; }
    /// <summary>The graphics card's memory in use and all it has, MB.</summary>
    public double? VramMb { get; init; }
    public double? VramTotalMb { get; init; }
    /// <summary>What the drives read and wrote, MB a second.</summary>
    public double? DiskMbs { get; init; }
    /// <summary>Download and upload, megabits a second (as a connection's speed is said).</summary>
    public double? DownMbps { get; init; }
    public double? UpMbps { get; init; }
    public int? Battery { get; init; }
    public bool? OnBattery { get; init; }
    public string? Front { get; init; }
    public bool Here { get; init; }
    /// <summary>Each fan's average speed in the minute, by the fan's name, in the order the PC lists them.</summary>
    public List<(string Name, int Rpm)> Fans { get; init; } = [];
    public List<CaseApp> CpuApps { get; init; } = [];
    public List<CaseApp> GpuApps { get; init; } = [];
    public List<CaseApp> MemApps { get; init; } = [];
    /// <summary>Warnings for this minute alone ("CPU up 22° in 5 minutes"), at most two.</summary>
    public List<string> Flags { get; init; } = [];
}

/// <summary>One line on a lane: what it is, the palette brush it is drawn in, and its value for each minute (null: a break).</summary>
public sealed record CaseSeries(string Label, string Brush, double?[] Values);

/// <summary>One lane: a name, its lines, and the scale they share (from 0 to <see cref="Max"/>, said in <see cref="Unit"/>).</summary>
public sealed record CaseLane(string Name, string Unit, double Max, List<CaseSeries> Series);

/// <summary>A stretch of a strip with one app (null: nobody, or none clearly), and the app's own colour.</summary>
public sealed record CaseBand(DateTime From, DateTime To, string? App, string? Color);

/// <summary>A strip under the lanes: which app was in front, or working a part hardest, minute by minute.</summary>
public sealed record CaseStrip(string Name, List<CaseBand> Bands);

/// <summary>
/// Something that happened in those minutes, marked across every lane: an app opening (<see cref="Label"/> is its
/// name), or the crash itself ("Crash").
/// </summary>
public sealed record CaseMark(DateTime At, string Label, bool IsCrash);

/// <summary>
/// The box beside the lanes: every reading of the minute pointed at, or of the crash's own minute. Always the same
/// lines in the same places, whatever the minute (a dash where nothing was recorded): the box never changes size.
/// </summary>
public sealed partial class CaseReadout : ObservableObject
{
    private readonly IReadOnlyList<CaseMinute> _minutes;
    private readonly int _crash;

    private readonly DateTime? _crashAt;
    private readonly bool _seconds;
    private readonly string _word;

    /// <param name="seconds">The close-up: a reading every two seconds, said to the second.</param>
    /// <param name="word">What it was, as said in the box: "crash", "shut-off", "blue screen".</param>
    public CaseReadout(IReadOnlyList<CaseMinute> minutes, int crashIndex, double? ramTotal = null, DateTime? crashAt = null, bool seconds = false, string word = "crash")
    {
        _word = word;
        _minutes = minutes;
        _seconds = seconds;
        RamTotal = ramTotal;
        _crashAt = crashAt;
        _crash = Math.Clamp(crashIndex, 0, Math.Max(0, minutes.Count - 1));
        if (minutes.Count > 0) Show(_crash, pointing: false);
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _when = "";
    [ObservableProperty] private string _flag1 = "";
    [ObservableProperty] private string _flag2 = "";
    [ObservableProperty] private bool _hasFlags;
    [ObservableProperty] private string _cpuTemp = "–";
    [ObservableProperty] private string _gpuTemp = "–";
    [ObservableProperty] private string _hotSpot = "–";
    [ObservableProperty] private string _cpuLoad = "–";
    [ObservableProperty] private string _gpuLoad = "–";
    [ObservableProperty] private string _ram = "–";
    [ObservableProperty] private string _commit = "–";
    [ObservableProperty] private string _vram = "–";
    [ObservableProperty] private string _battery = "–";
    [ObservableProperty] private string _disk = "–";
    [ObservableProperty] private string _down = "–";
    [ObservableProperty] private string _up = "–";
    [ObservableProperty] private string _front = "–";
    [ObservableProperty] private string _here = "–";
    [ObservableProperty] private IReadOnlyList<CaseApp> _fans = [];
    [ObservableProperty] private IReadOnlyList<CaseApp> _cpuApps = [];
    [ObservableProperty] private IReadOnlyList<CaseApp> _gpuApps = [];
    [ObservableProperty] private IReadOnlyList<CaseApp> _memApps = [];

    /// <summary>The installed memory, GB, for "of 32 GB" (null when not known).</summary>
    public double? RamTotal { get; }

    /// <summary>Lines kept for each list, used or not.</summary>
    public const int FanLines = 3, CpuLines = 3, GpuLines = 2, MemLines = 3;

    /// <summary>The minute at <paramref name="index"/> (pointing) or, with none, the crash's own minute.</summary>
    public void Show(int? index, bool pointing = true)
    {
        if (_minutes.Count == 0) return;
        int i = index is int at ? Math.Clamp(at, 0, _minutes.Count - 1) : _crash;
        pointing &= index is not null;
        var m = _minutes[i];
        string said = m.Time.ToString(_seconds ? "h:mm:ss tt" : "h:mm tt");
        if (_seconds)
        {
            // To the second: the reading nearest the crash, then how far from it each other one is.
            Title = pointing ? $"At {said}" : $"Last reading before the {_word}, {said}";
            double from = ((_crashAt ?? _minutes[_crash].Time) - m.Time).TotalSeconds;
            When = !pointing ? "Point at the lanes for any other moment" : Math.Abs(from) < 1.5 ? $"The {_word}'s own moment"
                : from > 0 ? $"{Ago(from)} before the {_word}" : $"{Ago(-from)} after it";
        }
        else
        {
            // The crash's own minute where it was recorded; else the last before it (a PC that went off records nothing after).
            bool own = _crashAt is not DateTime crashAt || m.Time.AddMinutes(1) > crashAt;
            Title = pointing ? $"At {said}" : own ? $"At the {_word}, {said}" : $"Just before the {_word}, {said}";
            int before = (int)Math.Round((_minutes[_crash].Time - m.Time).TotalMinutes);
            When = !pointing ? "Point at the lanes for any other minute" : before > 0 ? $"{before} min before the {_word}" : before < 0 ? $"{-before} min after it" : $"The {_word}'s own minute";
        }
        Flag1 = m.Flags.ElementAtOrDefault(0) ?? (_seconds ? "Nothing stands out at this moment" : "Nothing stands out in this minute");
        Flag2 = m.Flags.ElementAtOrDefault(1) ?? "";
        HasFlags = m.Flags.Count > 0;
        CpuTemp = Temp(m.CpuTemp);
        GpuTemp = Temp(m.GpuTemp);
        HotSpot = Temp(m.GpuHotSpot);
        CpuLoad = Load(m.CpuLoad, m.CpuPower);
        GpuLoad = Load(m.GpuLoad, m.GpuPower);
        Ram = m.RamGb is double gb ? RamTotal is double total ? $"{gb:0.0} of {total:0} GB" : $"{gb:0.0} GB" : "–";
        Commit = m.CommitPct is double c ? $"{c:0}%" : "–";
        Vram = m.VramMb is double v ? m.VramTotalMb is double all && all > 0 ? $"{v / 1024:0.0} of {all / 1024:0} GB" : $"{v / 1024:0.0} GB" : "–";
        Battery = m.Battery is int charge ? $"{charge}%{(m.OnBattery == true ? ", on battery" : "")}" : m.OnBattery is null ? "No battery" : "–";
        Disk = m.DiskMbs is double d ? $"{d:0.#} MB/s" : "–";
        Down = m.DownMbps is double down ? $"{down:0.#} Mbps" : "–";
        Up = m.UpMbps is double up ? $"{up:0.#} Mbps" : "–";
        Front = m.Front ?? "Nothing";
        Here = m.Here ? "Yes" : "Away";
        Fans = Pad([.. m.Fans.Select(f => new CaseApp(f.Name, $"{f.Rpm:N0} rpm"))], FanLines);
        CpuApps = Pad(m.CpuApps, CpuLines);
        GpuApps = Pad(m.GpuApps, GpuLines);
        MemApps = Pad(m.MemApps, MemLines);
    }

    private static string Temp(double? t) => t is double v ? Units.TempShort(v) : "–";

    /// <summary>"28 s", "1 min 12 s".</summary>
    private static string Ago(double seconds)
    {
        int s = (int)Math.Round(seconds);
        return s < 60 ? $"{s} s" : s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
    }

    private static string Load(double? load, double? power) => (load, power) switch
    {
        (double l, double p) => $"{l:0}% · {p:0} W",
        (double l, null) => $"{l:0}%",
        (null, double p) => $"{p:0} W",
        _ => "–",
    };

    /// <summary>Exactly <paramref name="lines"/> lines: the first ones, then dashes.</summary>
    internal static IReadOnlyList<CaseApp> Pad(IReadOnlyList<CaseApp> items, int lines) =>
        [.. items.Take(lines), .. Enumerable.Repeat(new CaseApp("–", ""), Math.Max(0, lines - items.Count))];
}

/// <summary>
/// One way of looking at the moments around a crash: the 45 minutes before and 10 after, a reading a minute; or the
/// close-up, a reading about every two seconds. Everything the lanes and the box beside them draw.
/// </summary>
public sealed class CaseView
{
    public List<CaseMinute> Minutes { get; init; } = [];
    public List<CaseLane> Lanes { get; init; } = [];
    public List<CaseStrip> Strips { get; init; } = [];
    public List<CaseMark> Marks { get; init; } = [];
    public List<CaseGap> Gaps { get; init; } = [];
    /// <summary>What stood out, in words, above the lanes (the last line: what was calm).</summary>
    public List<Core.Stability.StoodOut> StoodOut { get; init; } = [];
    public CaseReadout? Readout { get; init; }
    public DateTime ChartFrom { get; init; }
    public DateTime ChartTo { get; init; }
    /// <summary>Readings further apart than this are a break in a line (seconds).</summary>
    public double BreakSec { get; init; } = 90;
    /// <summary>A time along the foot every so often.</summary>
    public TimeSpan TickEvery { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>"one reading a minute", "a reading every 2 to 3 seconds".</summary>
    public string Rate { get; init; } = "one reading a minute";
    public bool IsCloseUp { get; init; }
    public bool HasChart => Minutes.Count > 0;

    /// <summary>The crash's own time on the lanes.</summary>
    public DateTime? Crash => Marks.FirstOrDefault(k => k.IsCrash)?.At;

    /// <summary>Above the lanes, at the right: the stretch they cover.</summary>
    public string RangeText => Minutes.Count == 0 ? "" : $"{ChartFrom.ToString(IsCloseUp ? "h:mm:ss tt" : "h:mm tt")} to {ChartTo.ToString(IsCloseUp ? "h:mm:ss tt" : "h:mm tt")} · {Rate}";
}
