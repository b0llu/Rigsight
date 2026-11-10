using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Every action that removes something is a red outlined button that fills red under the pointer (the user's rule,
/// 2026-11-10): readable in every theme, at rest and filled, and drawn so it can be looked at.
/// </summary>
[Collection("UI")]
public sealed class DangerButtonLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    [Fact]
    public void Remove_buttons_read_in_every_theme_at_rest_and_filled()
    {
        _ = host; // the real app, with its styles
        Directory.CreateDirectory(Shots);
        var problems = new List<string>();
        try
        {
            foreach (var theme in ThemeCatalog.All)
            {
                Ui.Run(() =>
                {
                    ThemeManager.Apply(theme.Key);
                    var res = Application.Current.Resources;
                    var hot = (Color)res["HotColor"];
                    var surface = (Color)res["SurfaceColor"];
                    var onHot = ((SolidColorBrush)res["OnHotBrush"]).Color;
                    double rest = Accents.Contrast(hot, surface), filled = Accents.Contrast(onHot, hot);
                    if (rest < 4.5) problems.Add($"{theme.Key}: red words on the card {rest:0.00}:1");
                    if (filled < 4.5) problems.Add($"{theme.Key}: words on the red fill {filled:0.00}:1");

                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
                    row.Children.Add(Make("DangerButton", "Remove", hover: false));
                    row.Children.Add(Make("DangerButton", "Remove", hover: true));
                    row.Children.Add(Make("DangerButton", WithBin("Clear all history…"), hover: false));
                    row.Children.Add(Make("DangerButton", WithBin("Clear all history…"), hover: true));
                    row.Children.Add(Make("DangerIconButton", "\uE74D", hover: false));
                    row.Children.Add(Make("DangerIconButton", "\uE74D", hover: true));
                    row.Children.Add(Make("DangerIconButton", "\uE711", hover: false));
                    var card = new Border { Child = row };
                    card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                    var window = new Window { Content = card, SizeToContent = SizeToContent.WidthAndHeight, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                    window.Show();
                    window.UpdateLayout();
                    Ui.SavePng(card, Path.Combine(Shots, $"remove-buttons-{theme.Key}.png"));
                    window.Close();
                });
            }
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static object WithBin(string words)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = "\uE74D", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.StyleProperty, "Icon");
        icon.FontSize = 12;
        p.Children.Add(icon);
        p.Children.Add(new TextBlock { Text = words });
        return p;
    }

    /// <summary>A remove button; "hover" draws it as it is under the pointer (the fill and words its trigger sets).</summary>
    private static Button Make(string style, object content, bool hover)
    {
        var b = new Button { Content = content, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        b.Style = (Style)Application.Current.FindResource(style);
        if (hover)
        {
            b.SetResourceReference(Control.BackgroundProperty, "HotBrush");
            b.SetResourceReference(Control.ForegroundProperty, "OnHotBrush");
        }
        return b;
    }
}
