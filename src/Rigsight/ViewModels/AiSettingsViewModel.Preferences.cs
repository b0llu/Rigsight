using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.ViewModels;

/// <summary>A language answers can be written in: its name for the prompt ("" for the one they write in), and as shown.</summary>
public sealed record AnswerLanguage(string Code, string Name);

/// <summary>
/// Settings › Ask RigSight › How it answers (approved mock-up 2026-10-24), for Ollama and my own key: length, tone,
/// language and a few words about them, added to RigSight's instructions and never in place of them. The full text
/// stays behind "Edit the full instructions".
/// </summary>
public sealed partial class AiSettingsViewModel
{
    private AiPreferences Preferences => Ai.PreferencesFor(EditMode);

    private void SetPreference(Action<AiPreferences> change, string property)
    {
        _settings.Update(s => change(s.Ai.PreferencesFor(EditMode)));
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(PreferencesChanged));
    }

    /// <summary>"short", "normal" or "detailed", for the segments.</summary>
    public string AnswerLength
    {
        get => Preferences.Length;
        set { if (value is "short" or "normal" or "detailed" && value != Preferences.Length) { Log.Write("settings", $"Answer length ({EditMode}): {value}"); SetPreference(p => p.Length = value, nameof(AnswerLength)); } }
    }

    /// <summary>"friendly", "direct" or "technical", for the segments.</summary>
    public string Tone
    {
        get => Preferences.Tone;
        set { if (value is "friendly" or "direct" or "technical" && value != Preferences.Tone) { Log.Write("settings", $"Answer tone ({EditMode}): {value}"); SetPreference(p => p.Tone = value, nameof(Tone)); } }
    }

    /// <summary>The languages answers can be written in, each in its own words; the first is "the one I write in".</summary>
    public IReadOnlyList<AnswerLanguage> AnswerLanguages { get; } =
    [
        new("", Lang.T("The same as I write")),
        new("English", "English"), new("Spanish", "Español"), new("French", "Français"), new("German", "Deutsch"),
        new("Italian", "Italiano"), new("Portuguese", "Português"), new("Polish", "Polski"), new("Turkish", "Türkçe"),
        new("Russian", "Русский"), new("Hindi", "हिन्दी"), new("Japanese", "日本語"), new("Korean", "한국어"), new("Chinese", "中文"),
    ];

    public string AnswerLanguageCode
    {
        get => Preferences.Language;
        set { value ??= ""; if (value != Preferences.Language) { Log.Write("settings", $"Answer language ({EditMode}): {(value.Length > 0 ? value : "as written")}"); SetPreference(p => p.Language = value, nameof(AnswerLanguageCode)); } }
    }

    /// <summary>About them and their PC, up to 1,500 characters (the box stops there).</summary>
    public string AboutYou
    {
        get => Preferences.About;
        set
        {
            value ??= "";
            if (value.Length > AiPreferences.AboutMax) value = value[..AiPreferences.AboutMax];
            if (value == Preferences.About) return;
            SetPreference(p => p.About = value, nameof(AboutYou));
            OnPropertyChanged(nameof(AboutCount));
        }
    }

    public int AboutMax => AiPreferences.AboutMax;
    public string AboutCount => $"{AboutYou.Length:N0} / {AiPreferences.AboutMax:N0}";

    /// <summary>Any of them changed from RigSight's: "Reset to RigSight's" shows.</summary>
    public bool PreferencesChanged => CanEditInstructions && !Preferences.IsDefault;

    [RelayCommand]
    private void ResetPreferences()
    {
        Log.Write("settings", $"Answer preferences ({EditMode}) back to RigSight's");
        _settings.Update(s => { var p = s.Ai.PreferencesFor(EditMode); p.Length = "normal"; p.Tone = "friendly"; p.Language = ""; p.About = ""; });
        foreach (var name in new[] { nameof(AnswerLength), nameof(Tone), nameof(AnswerLanguageCode), nameof(AboutYou), nameof(AboutCount), nameof(PreferencesChanged) })
            OnPropertyChanged(name);
    }
}
