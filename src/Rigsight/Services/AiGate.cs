using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;

namespace Rigsight.Services;

/// <summary>
/// A question an AI button hands to RigSight AI, about the page it's on: "How can I make Dota 2 run better?" on Games.
/// <paramref name="Explain"/>: an Explain button, opened as "Explain ..." on the thing itself (a crash).
/// </summary>
public sealed record AiQuestion(string Text, string Page, bool Explain = false);

/// <summary>
/// Every AI button's one switch (the AiButton style reads it, 2026-10-26): shown only while the AI is on, and only for a
/// page whose records answers may read, the moment either changes, with no restart. Pressing one goes to the Ask
/// window, which checks the AI is on again before it opens. Set by <see cref="ViewModels.AskViewModel"/>.
/// </summary>
public static class AiGate
{
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// <summary>The AI is on: switched on for this PC and not set to Off.</summary>
    public static bool IsOn { get; private set; }
    /// <summary>Goes up whenever what's shared changes, so every button looks again.</summary>
    public static int Version { get; private set; }

    private static Func<string, bool> _sends = _ => true;
    private static Action<AiQuestion>? _ask;

    /// <summary>What every AI button runs: its <see cref="AiQuestion"/>, to the Ask window.</summary>
    public static RelayCommand<AiQuestion> Ask { get; } = new(q => { if (q is not null && IsOn) _ask?.Invoke(q); });

    public static void Update(bool on, Func<string, bool> sends, Action<AiQuestion> ask)
    {
        _sends = sends;
        _ask = ask;
        IsOn = on;
        Version++;
        StaticPropertyChanged?.Invoke(null, new(nameof(IsOn)));
        StaticPropertyChanged?.Invoke(null, new(nameof(Version)));
    }

    public static bool Shows(AiQuestion? q) => IsOn && (q is null || _sends(q.Page));
}

/// <summary>An AI button's visibility: [IsOn, Version, its question] → shown or collapsed (see <see cref="AiGate"/>).</summary>
public sealed class AiShowConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is true && AiGate.Shows(values.Length > 2 ? values[2] as AiQuestion : null) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
