using System.Text.RegularExpressions;
using Rigsight.Core;
using Rigsight.Tests.Core;

namespace Rigsight.Tests.App;

/// <summary>
/// The audit of 2026-11-05: coloured text readable in every theme, and every text size and corner taken from one place
/// (Controls/Sizes.cs) rather than typed in again.
/// </summary>
public sealed class ReadabilityTests
{
    private static string Themes => Path.Combine(Repo.Root, "src", "Rigsight", "Themes");

    private static Dictionary<string, string> Palette(string theme) =>
        Regex.Matches(File.ReadAllText(Path.Combine(Themes, theme + ".xaml")), @"<Color x:Key=""(\w+)Color"">#([0-9A-Fa-f]{6})<")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static double Lum(string hex)
    {
        double Lin(int i)
        {
            double v = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(0) + 0.7152 * Lin(2) + 0.0722 * Lin(4);
    }

    internal static double Contrast(string a, string b)
    {
        double x = Lum(a.TrimStart('#')), y = Lum(b.TrimStart('#'));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    public static TheoryData<string> AllThemes => [.. Rigsight.Core.Settings.ThemeCatalog.All.Select(t => t.File)];

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void Coloured_text_reads_on_cards_and_tiles_in_every_theme(string theme)
    {
        var p = Palette(theme);
        // The two for low vision read at 7:1 (WCAG's stricter level), the rest at 4.5:1.
        double least = theme is "Contrast" or "Bright" ? 7 : 4.5;
        // Every colour drawn as text somewhere (a warm peak, a green "done", the GPU label...), on the page, a card, a tile
        // in it, and the hover shade a row takes under the pointer (the user, 2026-11-05: "hover too").
        foreach (var key in new[] { "Text", "Muted", "Faint", "Cpu", "Gpu", "Warm", "Hot", "Purple", "Ai", "Orange", "Cool", "Good", "Pink", "Sky" })
            foreach (var surface in new[] { "Bg", "Surface", "Surface2", "Hover" })
            {
                if (!p.TryGetValue(key, out var c)) continue;
                double ratio = Contrast(c, p[surface]);
                Assert.True(ratio >= least, $"{theme}: {key} #{c} on {surface} #{p[surface]} is {ratio:0.00}:1");
            }
    }

    [Fact]
    public void Every_accent_reads_in_every_theme()
    {
        // A main button's text sits on the accent, a switch's knob too, and links are drawn in it: the deeper shade on
        // the light themes' cards and tiles (and white), the lighter one on each dark theme's.
        foreach (var (key, _, onDark, onLight) in AccentColors.Named)
        {
            Assert.True(Contrast(onLight, "#FFFFFF") >= 4.5, $"{key} {onLight}: {Contrast(onLight, "#FFFFFF"):0.00}:1 on white");
            foreach (var theme in Rigsight.Core.Settings.ThemeCatalog.All.Where(t => t.IsLight).Select(t => t.File))
            {
                var p = Palette(theme);
                foreach (var surface in new[] { "Surface", "Surface2" })
                    Assert.True(Contrast(onLight, p[surface]) >= 4.5, $"{key} {onLight} on {theme} {surface}: {Contrast(onLight, p[surface]):0.00}:1");
            }
            foreach (var theme in Rigsight.Core.Settings.ThemeCatalog.All.Where(t => !t.IsLight).Select(t => t.File))
            {
                var p = Palette(theme);
                foreach (var surface in new[] { "Surface", "Surface2" })
                    Assert.True(Contrast(onDark, p[surface]) >= 4.5, $"{key} {onDark} on {theme} {surface}: {Contrast(onDark, p[surface]):0.00}:1");
            }
        }
    }

    [Fact]
    public void Text_sizes_and_corners_come_from_one_place()
    {
        var src = Path.Combine(Repo.Root, "src", "Rigsight");
        var typed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.xaml", SearchOption.AllDirectories))
        {
            // A drawn miniature keeps its own scale.
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || Path.GetFileName(file) == "FeaturePicture.xaml") continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"FontSize=""\d|Property=""(?:TextElement\.)?FontSize"" Value=""\d"))
                typed.Add($"{Path.GetFileName(file)}:{text[..m.Index].Count(c => c == '\n') + 1}");
        }
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            // The Rewind painter draws a picture of its own size; Sizes.cs is where the values live.
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains(@"\Rewind\") || Path.GetFileName(file) == "Sizes.cs") continue;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"FontSize\s*=\s*\d"))
                typed.Add($"{Path.GetFileName(file)}:{text[..m.Index].Count(c => c == '\n') + 1}");
        }
        Assert.True(typed.Count == 0, "Typed-in sizes (use ctl:TextSize, ctl:IconSize or ctl:Corner): " + string.Join(", ", typed));
    }
}
