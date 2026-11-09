using System.Windows;
using System.Windows.Controls;

namespace Rigsight.Controls;

/// <summary>
/// One setting as a row (approved mock-up BrxrxUeSZAHcqXGrxtt4LC, 2026-11-09; Windows' own settings cards): its icon in the
/// app's icon box, its name, a line under it, and its control (the content) on the right. A row under another in the same
/// card draws a line between them (<see cref="Divider"/>). Its look is in Theme.xaml.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(nameof(Line), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DividerProperty = DependencyProperty.Register(nameof(Divider), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    /// <summary>The icon's glyph (Segoe Fluent Icons).</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>The line under the name (none: the name alone).</summary>
    public string Line { get => (string)GetValue(LineProperty); set => SetValue(LineProperty, value); }

    /// <summary>A line above it, between it and the row before it in the same card.</summary>
    public bool Divider { get => (bool)GetValue(DividerProperty); set => SetValue(DividerProperty, value); }
}
