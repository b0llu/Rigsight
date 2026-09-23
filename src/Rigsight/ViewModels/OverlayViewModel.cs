using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One reading the overlay can show, as an on/off chip.</summary>
public sealed partial class OverlayMetricOption(OverlayMetric metric, string label, SettingsModel settings) : ObservableObject
{
    public string Label { get; } = label;

    public bool IsOn
    {
        get => settings.Current.Overlay.Metrics.Contains(metric);
        set => settings.Update(s =>
        {
            var list = s.Overlay.Metrics;
            list.Remove(metric);
            if (value) list.Add(metric);
            list.Sort();
        });
    }

    public void Refresh() => OnPropertyChanged(nameof(IsOn));
}

public sealed record OverlayMetricGroup(string Title, IReadOnlyList<OverlayMetricOption> Options);

/// <summary>The Overlay page: the shortcut, what the overlay shows, and how it looks.</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly AgentClient _client;

    public OverlayViewModel(SettingsModel settings, AgentClient client)
    {
        _settings = settings;
        _client = client;
        OverlayMetricOption O(OverlayMetric m, string label) => new(m, label, settings);
        Groups =
        [
            new("CPU", [O(OverlayMetric.CpuTemp, "Temperature"), O(OverlayMetric.CpuLoad, "Load"), O(OverlayMetric.CpuClock, "Clock speed"), O(OverlayMetric.CpuPower, "Power")]),
            new("GPU", [O(OverlayMetric.GpuTemp, "Temperature"), O(OverlayMetric.GpuHotSpot, "Hot spot"), O(OverlayMetric.GpuLoad, "Load"),
                        O(OverlayMetric.GpuClock, "Clock speed"), O(OverlayMetric.GpuPower, "Power"), O(OverlayMetric.GpuMemory, "Video memory")]),
            new("MEMORY", [O(OverlayMetric.Ram, "RAM in use")]),
            new("OTHER", [O(OverlayMetric.Session, "App and time on it"), O(OverlayMetric.Clock, "Time of day")]),
        ];
    }

    private OverlaySettings Config => _settings.Current.Overlay;

    private void Change(Action<OverlaySettings> change, string property)
    {
        _settings.Update(s => change(s.Overlay));
        OnPropertyChanged(property);
    }

    public IReadOnlyList<OverlayMetricGroup> Groups { get; }

    public bool Enabled
    {
        get => Config.Enabled;
        set { Change(c => c.Enabled = value, nameof(Enabled)); OnPropertyChanged(nameof(Intro)); }
    }

    public string Hotkey => Config.Hotkey;

    public string Intro => Enabled
        ? $"Press {Hotkey} in any game to show or hide a small readout in the corner of the screen. It never takes focus, and clicks pass straight through it."
        : "The shortcut is off. You can still show the overlay from here or from the tray icon's menu.";

    public string Corner { get => Config.Corner.ToString(); set => Change(c => c.Corner = Enum.Parse<OverlayCorner>(value), nameof(Corner)); }
    public string Layout { get => Config.Layout.ToString(); set => Change(c => c.Layout = Enum.Parse<OverlayLayout>(value), nameof(Layout)); }
    public double OpacityPercent { get => Math.Round(Config.Opacity * 100); set => Change(c => c.Opacity = value / 100, nameof(OpacityPercent)); }
    public string Scale
    {
        get => Config.Scale.ToString("0.##", CultureInfo.InvariantCulture);
        set => Change(c => c.Scale = double.Parse(value, CultureInfo.InvariantCulture), nameof(Scale));
    }

    // ── Fullscreen games (RivaTuner) ──

    /// <summary>running · stopped · missing · installing · install-failed (from the agent; empty until it says).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRtssWarning), nameof(RtssTitle), nameof(RtssStatus), nameof(CanInstallRtss), nameof(CanStartRtss))]
    private string _rtssState = "";

    /// <summary>Fullscreen games need RivaTuner; say so only when it isn't ready.</summary>
    public bool ShowRtssWarning => RtssState is not ("" or "running");
    public bool CanInstallRtss => RtssState is "missing" or "install-failed";
    public bool CanStartRtss => RtssState == "stopped";

    public string RtssTitle => RtssState == "installing" ? "Installing RivaTuner…" : "The overlay won't show in fullscreen games";

    public string RtssStatus => RtssState switch
    {
        "stopped" => "Fullscreen games hide every window, so Rigsight shows the overlay inside them through RivaTuner Statistics Server, and it isn't running. Start it, then restart any game that's already open.",
        "installing" => "This takes a minute. Restart any game that's already open once it's done.",
        "install-failed" => "Couldn't install RivaTuner automatically. Download it from Guru3D and install it, then come back here.",
        _ => "Fullscreen games hide every window, so Rigsight shows the overlay inside them through RivaTuner Statistics Server, a free tool that anti-cheat accepts. It isn't installed yet.",
    };

    [RelayCommand]
    private void StartRtss() => _client.SendCommand("start-rtss");

    [RelayCommand]
    private void InstallRtss() => _client.SendCommand("install-rtss");

    [RelayCommand]
    private static void OpenRtssDownload() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/") { UseShellExecute = true })?.Dispose();

    /// <summary>Asks the agent for the latest shortcut and RivaTuner state (RivaTuner may have been installed meanwhile).</summary>
    public void RequestStatus() => _client.SendCommand("overlay-status");

    // ── State reported by the agent ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isVisible;

    [ObservableProperty] private bool _hotkeyTaken;

    public string ToggleText => IsVisible ? "Hide overlay" : "Show overlay";

    [RelayCommand]
    private void Toggle() => _client.SendCommand("overlay-toggle");

    // ── Recording a new shortcut ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordText))]
    private bool _isRecording;

    /// <summary>Why the last key press wasn't accepted, if it wasn't.</summary>
    [ObservableProperty] private string _recordHint = "";

    public string RecordText => IsRecording ? "Press the new shortcut…" : Hotkey;

    [RelayCommand]
    private void StartRecording()
    {
        IsRecording = true;
        RecordHint = "Hold Ctrl, Alt or Shift and press a key. Esc cancels.";
    }

    public void CancelRecording()
    {
        IsRecording = false;
        RecordHint = "";
    }

    /// <summary>Called with each key pressed while recording (Windows virtual-key code). Modifier-only presses are ignored by the caller.</summary>
    public void Record(int virtualKey, HotkeyModifiers modifiers)
    {
        var hotkey = new Hotkey(modifiers, virtualKey);
        if (!Core.Settings.Hotkey.IsSupportedKey(virtualKey))
        {
            RecordHint = "That key can't be used. Try a letter, a number or an F-key.";
            return;
        }
        if (!hotkey.IsValid)
        {
            RecordHint = "Add Ctrl, Alt or Shift, so the shortcut doesn't clash with your games.";
            return;
        }
        Change(c => c.Hotkey = hotkey.ToString(), nameof(Hotkey));
        IsRecording = false;
        RecordHint = "";
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(RecordText));
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        Change(c => c.Hotkey = OverlaySettings.DefaultHotkey, nameof(Hotkey));
        CancelRecording();
        OnPropertyChanged(nameof(Intro));
        OnPropertyChanged(nameof(RecordText));
    }

    // ── Preview ──

    [ObservableProperty] private ImageSource? _preview;

    /// <summary>Asks the agent to draw fresh previews (the overlay's included).</summary>
    public void RequestPreview() => _client.SendCommand("render-previews");

    public void OnPreviewsReady() => Preview = PreviewImages.Load("Overlay") ?? Preview;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var group in Groups)
            foreach (var option in group.Options) option.Refresh();
    }
}
