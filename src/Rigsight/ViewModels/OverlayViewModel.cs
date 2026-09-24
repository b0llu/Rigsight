using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Settings;
using Rigsight.Models;
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

/// <summary>One of the user's sensors on the overlay: what it is, and the short name shown for it.</summary>
public sealed partial class OverlaySensorRow(string id, string name, string hardware, bool found, SettingsModel settings) : ObservableObject
{
    public string Id { get; } = id;
    /// <summary>The sensor's own name (or the one given on All sensors): shown when no short name is set.</summary>
    public string Name { get; } = name;
    public string Hardware { get; } = found ? hardware : "Not found on this PC right now";
    public bool Found { get; } = found;

    /// <summary>The short name on the overlay; empty uses <see cref="Name"/>.</summary>
    public string Label
    {
        get => settings.Current.Overlay.Sensors.FirstOrDefault(s => s.Id == Id)?.Label ?? "";
        set
        {
            var label = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            settings.Update(s =>
            {
                if (s.Overlay.Sensors.FirstOrDefault(x => x.Id == Id) is { } x) x.Label = label;
            });
            OnPropertyChanged();
        }
    }
}

/// <summary>The Overlay page: the shortcut, what the overlay shows, and how it looks.</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly AgentClient _client;

    public OverlayViewModel(SettingsModel settings, AgentClient client, LiveData live)
    {
        _settings = settings;
        _client = client;
        _live = live;
        OverlayMetricOption O(OverlayMetric m, string label) => new(m, label, settings);
        Groups =
        [
            new("GAME", [O(OverlayMetric.Fps, "Frame rate (FPS)"), O(OverlayMetric.FrameTime, "Frame time"), O(OverlayMetric.OnePercentLow, "1% low")]),
            new("CPU", [O(OverlayMetric.CpuTemp, "Temperature"), O(OverlayMetric.CpuLoad, "Load"), O(OverlayMetric.CpuClock, "Clock speed"), O(OverlayMetric.CpuPower, "Power")]),
            new("GPU", [O(OverlayMetric.GpuTemp, "Temperature"), O(OverlayMetric.GpuHotSpot, "Hot spot"), O(OverlayMetric.GpuLoad, "Load"),
                        O(OverlayMetric.GpuClock, "Clock speed"), O(OverlayMetric.GpuPower, "Power"), O(OverlayMetric.GpuMemory, "Video memory")]),
            new("MEMORY", [O(OverlayMetric.Ram, "RAM in use")]),
            new("OTHER", [O(OverlayMetric.Session, "App and time on it"), O(OverlayMetric.Clock, "Time of day")]),
        ];
    }

    private readonly LiveData _live;

    private OverlaySettings Config => _settings.Current.Overlay;

    // ── The user's own sensors ──

    /// <summary>Every sensor on this PC, for the picker.</summary>
    public IReadOnlyList<SensorItem> AllSensors => _live.AllSensors;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSensorCommand))]
    private SensorItem? _sensorToAdd;

    public ObservableCollection<OverlaySensorRow> Sensors { get; } = [];

    public int MaxSensors => OverlaySettings.MaxSensors;
    public bool HasSensors => Sensors.Count > 0;
    public bool CanAddMoreSensors => Sensors.Count < OverlaySettings.MaxSensors;
    public string SensorsCount => $"{Sensors.Count} of {OverlaySettings.MaxSensors}";

    private bool CanAddSensor() => SensorToAdd is not null && CanAddMoreSensors && Sensors.All(r => r.Id != SensorToAdd.Id);

    [RelayCommand(CanExecute = nameof(CanAddSensor))]
    private void AddSensor()
    {
        if (SensorToAdd is not { } sensor) return;
        _settings.Update(s =>
        {
            if (s.Overlay.Sensors.Count < OverlaySettings.MaxSensors && s.Overlay.Sensors.All(x => x.Id != sensor.Id))
                s.Overlay.Sensors.Add(new OverlaySensor { Id = sensor.Id });
        });
        SensorToAdd = null;
        LoadSensors();
    }

    [RelayCommand]
    private void RemoveSensor(OverlaySensorRow row)
    {
        _settings.Update(s => s.Overlay.Sensors.RemoveAll(x => x.Id == row.Id));
        LoadSensors();
    }

    [RelayCommand]
    private void MoveSensorUp(OverlaySensorRow row) => MoveSensor(row, -1);

    [RelayCommand]
    private void MoveSensorDown(OverlaySensorRow row) => MoveSensor(row, 1);

    private void MoveSensor(OverlaySensorRow row, int by)
    {
        _settings.Update(s =>
        {
            var list = s.Overlay.Sensors;
            int i = list.FindIndex(x => x.Id == row.Id), j = i + by;
            if (i < 0 || j < 0 || j >= list.Count) return;
            (list[i], list[j]) = (list[j], list[i]);
        });
        LoadSensors();
    }

    /// <summary>Rebuilds the list from settings (only when it changed, so a name being typed isn't disturbed).</summary>
    public void LoadSensors()
    {
        var wanted = Config.Sensors;
        // Sensor identifiers aren't always unique (NVIDIA can list one twice): the first one wins.
        var names = new Dictionary<string, SensorItem>();
        foreach (var item in _live.AllSensors) names.TryAdd(item.Id, item);
        bool same = wanted.Count == Sensors.Count && wanted.Select(x => x.Id).SequenceEqual(Sensors.Select(r => r.Id))
                    && Sensors.All(r => r.Found == names.ContainsKey(r.Id));
        if (!same)
        {
            Sensors.Clear();
            foreach (var x in wanted)
            {
                bool found = names.TryGetValue(x.Id, out var item);
                Sensors.Add(new OverlaySensorRow(x.Id, item?.DisplayName ?? x.Label ?? x.Id, item?.HardwareName ?? "", found, _settings));
            }
        }
        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(CanAddMoreSensors));
        OnPropertyChanged(nameof(SensorsCount));
        OnPropertyChanged(nameof(AllSensors));
        AddSensorCommand.NotifyCanExecuteChanged();
    }

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
        LoadSensors();
    }
}
