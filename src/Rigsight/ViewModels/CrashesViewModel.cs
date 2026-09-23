using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public sealed partial class CrashesViewModel(ReportService reports) : ObservableObject
{
    /// <summary>"7", "30" or "90" days.</summary>
    [ObservableProperty] private string _range = "30";

    private List<CrashRow> _all = [];

    /// <summary>"All", "Apps" (app and game crashes, freezes) or "Pc" (blue screens, sudden shutdowns, GPU driver resets).</summary>
    [ObservableProperty] private string _filter = "All";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashes), nameof(Patterns), nameof(EmptyText))]
    private List<CrashRow> _crashes = [];

    [ObservableProperty] private int _appCrashCount;
    [ObservableProperty] private int _systemCount;
    [ObservableProperty] private int _driverResetCount;

    public bool HasCrashes => Crashes.Count > 0;

    public string EmptyText => Filter switch
    {
        "Apps" => "No app or game crashes in this period",
        "Pc" => "No PC crashes, driver resets or sudden shutdowns in this period",
        _ => "No crashes in this period",
    };

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter() => Crashes = Filter switch
    {
        "Apps" => [.. _all.Where(c => !c.IsSystem)],
        "Pc" => [.. _all.Where(c => c.IsSystem)],
        _ => _all,
    };

    /// <summary>Repeated causes worth pointing out ("3 of 4 shutdowns happened while asleep").</summary>
    public List<string> Patterns
    {
        get
        {
            var list = new List<string>();
            var shutdowns = Crashes.Where(c => c.Event.Kind == CrashKind.UnexpectedShutdown).ToList();
            int asleep = shutdowns.Count(c => c.Event.DuringSleep);
            if (asleep >= 2)
                list.Add($"{asleep} of {shutdowns.Count} unexpected shutdowns happened while the PC was asleep. If you switch off power at the wall, shut down fully first; otherwise a BIOS or chipset driver update often fixes sleep problems.");

            foreach (var g in Crashes.Where(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang)
                         .GroupBy(c => c.AppName ?? c.Event.AppExe).Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).Take(2))
                list.Add($"{g.Key} crashed {g.Count()} times. Most common cause: {g.GroupBy(c => c.Culprit).MaxBy(x => x.Count())!.Key.ToLowerInvariant()}.");

            int gpuRelated = Crashes.Count(c => c.Culprit.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                                c.Culprit.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                                c.Event.Kind == CrashKind.GpuDriverReset);
            if (gpuRelated >= 2)
                list.Add($"{gpuRelated} problems involved the graphics driver. A clean driver reinstall (and stock GPU clocks) is the first thing to try.");

            int hot = Crashes.Count(c => c.GpuBefore >= 83 || c.CpuBefore >= 88);
            if (hot >= 1)
                list.Add($"{hot} crash{(hot == 1 ? "" : "es")} happened while your hardware was running hot. Check airflow and fan curves.");
            return list;
        }
    }

    partial void OnRangeChanged(string value) => _ = LoadAsync();

    public async Task LoadAsync()
    {
        int days = int.TryParse(Range, out var d) ? d : 30;
        _all = await reports.CrashesAsync(DateTime.Today.AddDays(-(days - 1)), DateTime.Now.AddMinutes(1)) ?? [];
        AppCrashCount = _all.Count(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        SystemCount = _all.Count(c => c.Event.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown);
        DriverResetCount = _all.Count(c => c.Event.Kind == CrashKind.GpuDriverReset);
        ApplyFilter();
    }

    [RelayCommand]
    private static void OpenEventViewer()
    {
        try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); } catch { }
    }
}
