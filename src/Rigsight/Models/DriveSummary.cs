using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Protocol;

namespace Rigsight.Models;

/// <summary>Summary of one physical drive (from its SMART sensors and the agent's health check).</summary>
public sealed partial class DriveSummary : ObservableObject
{
    public DriveSummary(string name, SensorItem? temperature, SensorItem? life, SensorItem? usedSpace, SensorItem? powerOnHours)
    {
        Name = name;
        Temperature = temperature;
        Life = life;
        UsedSpace = usedSpace;
        PowerOnHours = powerOnHours;
        // The SSD's wear arrives with the readings, after this summary is made: follow it.
        if (life is not null)
            life.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(SensorItem.ShortValue) or "" or null)) return;
                OnPropertyChanged(nameof(HealthText));
                OnPropertyChanged(nameof(HealthBrush));
                OnPropertyChanged(nameof(HealthReason));
            };
    }

    public string Name { get; }
    public SensorItem? Temperature { get; }
    public SensorItem? Life { get; }
    public SensorItem? UsedSpace { get; }
    public SensorItem? PowerOnHours { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealth), nameof(HealthText), nameof(HealthBrush), nameof(SectorsText), nameof(HealthToolTip), nameof(HealthReason), nameof(SectorsBrush))]
    private DriveHealthInfo? _health;

    private string Status => Health?.Status ?? "Unknown";

    /// <summary>SSDs show their wear ("97%"); hard drives, which don't wear that way, show their SMART status.</summary>
    public bool HasHealth => Life is not null || Status != "Unknown";

    public string HealthText => Life is not null ? Life.ShortValue : Status;

    /// <summary>An SSD that reports this much of its rated life left (percent) or less gets a warning.</summary>
    public const double WornOut = 10;

    /// <summary>
    /// How the drive is doing, 0 good, 1 a warning, 2 bad: the worse of what SMART says and of the life an SSD says it
    /// has left. (The colour used to follow SMART alone, so an SSD at 1% of its life was shown in green.) The life
    /// figure is the drive's own and some models report it oddly, so by itself it is a warning and never "bad": only
    /// SMART reporting a failure is.
    /// </summary>
    private int Level => Math.Max(Status switch { "Bad" => 2, "Caution" => 1, _ => 0 }, Life?.Value is double left && left <= WornOut ? 1 : 0);

    public Brush HealthBrush => (Brush)Application.Current.FindResource(Level switch
    {
        2 => "HotBrush",
        1 => "WarmBrush",
        _ => "GoodBrush",
    });

    /// <summary>
    /// Why the drive isn't in green, said on its card in the same colour (nothing for a healthy drive): "Caution" or a
    /// low figure with no reason beside it leaves the reader asking what is wrong.
    /// </summary>
    public string? HealthReason
    {
        get
        {
            if (Status == "Bad") return "SMART reports a failure. Back up what's on it now.";
            bool sectors = Health is { } h && (h.ReallocatedSectors > 0 || h.PendingSectors > 0 || h.UncorrectableSectors > 0);
            if (Status == "Caution")
                return sectors ? "Bad sectors are an early warning. Keep a backup, and watch whether the count grows."
                    : "SMART reports early warning signs. Keep a backup.";
            // Said as what the drive reports, not as a verdict: its own health check may still say it is fine.
            if (Life?.Value is double left && left <= WornOut) return "The drive reports almost all of its rated life used. Keep a backup of what's on it.";
            return null;
        }
    }

    /// <summary>The sector line's colour: the warning's when there are bad sectors (they are the reason), faint when none.</summary>
    public Brush SectorsBrush => (Brush)Application.Current.FindResource(
        Health is { } h && (h.ReallocatedSectors > 0 || h.PendingSectors > 0 || h.UncorrectableSectors > 0) ? Status == "Bad" ? "HotBrush" : "WarmBrush" : "FaintBrush");

    /// <summary>"No bad sectors" / "3 reallocated sectors"… for drives that report them (SATA).</summary>
    public string? SectorsText
    {
        get
        {
            if (Health is not { } h || (h.ReallocatedSectors, h.PendingSectors, h.UncorrectableSectors) is (null, null, null)) return null;
            var parts = new List<string>();
            if (h.ReallocatedSectors > 0) parts.Add($"{h.ReallocatedSectors:N0} reallocated");
            if (h.PendingSectors > 0) parts.Add($"{h.PendingSectors:N0} pending");
            if (h.UncorrectableSectors > 0) parts.Add($"{h.UncorrectableSectors:N0} unreadable");
            return parts.Count == 0 ? "No bad sectors" : string.Join(", ", parts) + " sectors";
        }
    }

    public string HealthToolTip => Status switch
    {
        "Bad" => "SMART reports a serious problem with this drive. Back up anything important on it now.",
        "Caution" => "SMART reports early warning signs (such as bad sectors). Keep a backup and keep an eye on it.",
        "Good" when Life is null => "SMART reports no problems. Hard drives don't wear out like SSDs, so there's no percentage.",
        "Good" => "SMART reports no problems. The percentage is how much of its rated wear the SSD has left.",
        _ => "How much of its rated wear the SSD has left.",
    };
}
