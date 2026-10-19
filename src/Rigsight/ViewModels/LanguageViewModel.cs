using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A language in the picker: its own name, and its download size while it isn't downloaded. The same objects stay in
/// the list (a new list would leave the box showing nothing picked); only the note changes.
/// </summary>
public sealed partial class LanguageOption(string code, string name, bool isCurrent) : ObservableObject
{
    public string Code { get; } = code;
    public string Name { get; } = name;
    public bool IsCurrent { get; } = isCurrent;
    [ObservableProperty] private string _note = "";

    /// <summary>What a screen reader says for the item.</summary>
    public override string ToString() => Name;
}

public enum LanguageStep { None, Downloading, Ready, Failed, Outdated }

/// <summary>
/// Settings → General → Language, and the sidebar card when the newest pack couldn't be had after an update.
/// Picking a language downloads its pack (checked against GitHub), then RigSight asks to open again in it; English
/// needs no download. The tray, widgets and notifications switch at once, through the agent.
/// </summary>
public sealed partial class LanguageViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private string _target = Lang.Code;

    public LanguageViewModel(SettingsModel settings)
    {
        _settings = settings;
        Options = Build();
    }

    public IReadOnlyList<LanguageOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatus), nameof(IsDownloading), nameof(IsWarning), nameof(IsReady), nameof(StatusText),
        nameof(StatusDetail), nameof(ActionText), nameof(ShowCard), nameof(CanPick))]
    private LanguageStep _step;

    /// <summary>The language picked in the dropdown (the one in use until another is picked).</summary>
    public string Chosen
    {
        get => _target;
        set
        {
            if (value is null || value == _target) return;
            _target = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetName));
            _ = PickAsync(value);
        }
    }

    public bool CanPick => Step != LanguageStep.Downloading;
    public bool ShowStatus => Step is LanguageStep.Ready or LanguageStep.Failed or LanguageStep.Outdated;
    public bool IsDownloading => Step == LanguageStep.Downloading;
    public bool IsReady => Step == LanguageStep.Ready;
    public bool IsWarning => Step is LanguageStep.Failed or LanguageStep.Outdated;
    public bool ShowCard => Step == LanguageStep.Outdated;

    public string TargetName => LanguagePack.Known.FirstOrDefault(l => l.Code == _target)?.Name ?? _target;

    public string StatusText => Step switch
    {
        LanguageStep.Ready => Lang.T($"{TargetName} is ready."),
        LanguageStep.Failed => Lang.T($"Couldn't download {TargetName}."),
        LanguageStep.Outdated => Lang.T("The latest language pack couldn't be downloaded."),
        _ => "",
    };

    public string StatusDetail => Step switch
    {
        LanguageStep.Ready => Lang.T(LanguagePack.Known.FirstOrDefault(l => l.Code == _target)?.Reopens ?? ""),
        LanguageStep.Failed => Lang.T("Check the internet connection and try again."),
        LanguageStep.Outdated => Lang.T("Some new words show in English."),
        _ => "",
    };

    public string ActionText => Step == LanguageStep.Ready ? Lang.T($"Restart in {TargetName}") : Lang.T("Try again");

    /// <summary>
    /// At start: the pack in use isn't this version's (its download with the update failed, or the update was installed
    /// by hand). One more try where updates may reach GitHub by themselves; then the warning, until it's had.
    /// </summary>
    public async Task CheckAtStartAsync(bool mayCheck)
    {
        if (!LanguageStore.NewestMissing) return;
        bool had = await Task.Run(() => LanguageStore.RefreshAsync(mayCheck));
        if (!had && LanguageStore.NewestMissing) Step = LanguageStep.Outdated;
    }

    [RelayCommand]
    private async Task Act()
    {
        switch (Step)
        {
            case LanguageStep.Ready: Restart(); break;
            case LanguageStep.Failed: await PickAsync(_target); break;
            case LanguageStep.Outdated: await RetryNewestAsync(); break;
        }
    }

    private async Task PickAsync(string code)
    {
        if (code == Lang.Code && !LanguageStore.NewestMissing) { Step = LanguageStep.None; return; }
        if (code == Lang.Code) { Step = LanguageStep.Outdated; return; }
        if (code == LanguagePack.English || LanguageStore.Find(code) is { } here && !LanguageStore.IsOlder(here))
        {
            Step = LanguageStep.Ready;
            return;
        }
        Step = LanguageStep.Downloading;
        try
        {
            await Task.Run(() => LanguageStore.DownloadAsync(code));
            if (_target != code) return; // picked another meanwhile
            Refresh();
            Step = LanguageStep.Ready;
        }
        catch (Exception ex)
        {
            Log.Write("Language", $"Downloading the {code} pack failed: {ex.Message}");
            if (_target == code) Step = LanguageStep.Failed;
        }
    }

    /// <summary>The newest pack for the language in use, asked for again: when it comes, RigSight opens again with it.</summary>
    private async Task RetryNewestAsync()
    {
        string code = Lang.Code;
        Step = LanguageStep.Downloading;
        try
        {
            var pack = await Task.Run(() => LanguageStore.DownloadAsync(code));
            Step = LanguageStore.IsOlder(pack) ? LanguageStep.Outdated : LanguageStep.Ready;
        }
        catch (Exception ex)
        {
            Log.Write("Language", $"Downloading the newest {code} pack failed again: {ex.Message}");
            Step = LanguageStep.Outdated;
        }
    }

    /// <summary>Saves the language (the agent switches at once) and opens RigSight again in it.</summary>
    private void Restart()
    {
        string code = _target;
        _settings.Update(s => s.Language = code);
        _settings.Flush();
        Log.Write("Language", $"Opening again in {code}");
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                start.ArgumentList.Add("--after");
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add("--language");
                start.ArgumentList.Add(code);
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Language", ex);
        }
        Application.Current.Shutdown();
    }

    private static List<LanguageOption> Build()
    {
        var list = LanguagePack.Known.Select(l => new LanguageOption(l.Code, l.Name, l.Code == Lang.Code)).ToList();
        Refresh(list);
        return list;
    }

    private void Refresh() => Refresh(Options);

    /// <summary>A language not downloaded yet shows its size; English and downloaded ones show nothing.</summary>
    private static void Refresh(IEnumerable<LanguageOption> options)
    {
        foreach (var o in options)
        {
            var info = LanguagePack.Known.First(l => l.Code == o.Code);
            o.Note = o.Code == LanguagePack.English || LanguageStore.Find(o.Code) is not null ? "" : $"{info.SizeKB} KB";
        }
    }
}
