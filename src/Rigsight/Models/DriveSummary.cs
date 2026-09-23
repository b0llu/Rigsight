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
            life.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(SensorItem.ShortValue) or "" or null) OnPropertyChanged(nameof(HealthText)); };
    }

    public string Name { get; }
    public SensorItem? Temperature { get; }
    public SensorItem? Life { get; }
    public SensorItem? UsedSpace { get; }
    public SensorItem? PowerOnHours { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealth), nameof(HealthText), nameof(HealthBrush), nameof(SectorsText), nameof(HealthToolTip))]
    private DriveHealthInfo? _health;

    private string Status => Health?.Status ?? "Unknown";

    /// <summary>SSDs show their wear ("97%"); hard drives, which don't wear that way, show their SMART status.</summary>
    public bool HasHealth => Life is not null || Status != "Unknown";

    public string HealthText => Life is not null ? Life.ShortValue : Status;

    public Brush HealthBrush => (Brush)Application.Current.FindResource(Status switch
    {
        "Bad" => "HotBrush",
        "Caution" => "WarmBrush",
        _ => "GpuBrush",
    });

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
