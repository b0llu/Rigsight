using System.Drawing;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>App icons are kept for the few most recent apps only: an agent running for months must not keep them all.</summary>
public sealed class RecentIconsTests
{
    private static Bitmap New() => new(8, 8);

    private static bool Disposed(Bitmap b)
    {
        try { _ = b.Width; return false; }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public void Only_the_most_recent_are_kept_and_the_rest_released()
    {
        var icons = new RecentIcons(capacity: 3);
        var made = new Dictionary<string, Bitmap>();
        Bitmap Load(string p) => made[p] = New();
        foreach (var p in new[] { "a", "b", "c", "d", "e" }) icons.Get(p, Load);
        Assert.Equal(3, icons.Count);
        Assert.True(Disposed(made["a"]));
        Assert.True(Disposed(made["b"]));
        Assert.False(Disposed(made["e"]));
    }

    [Fact]
    public void Using_an_icon_keeps_it()
    {
        var icons = new RecentIcons(capacity: 2);
        int loads = 0;
        Bitmap Load(string p) { loads++; return New(); }
        var a = icons.Get("a", Load);
        icons.Get("b", Load);
        Assert.Same(a, icons.Get("A", Load)); // any case: exe paths
        icons.Get("c", Load);                 // "b" goes, not the just-used "a"
        Assert.Same(a, icons.Get("a", Load));
        Assert.Equal(3, loads);
        Assert.False(Disposed(a!));
    }

    [Fact]
    public void An_app_without_an_icon_is_remembered_too()
    {
        var icons = new RecentIcons(capacity: 4);
        int loads = 0;
        icons.Get("none", _ => { loads++; return null; });
        Assert.Null(icons.Get("none", _ => { loads++; return null; }));
        Assert.Equal(1, loads);
    }
}

[Collection("Drawing")]
public sealed class WidgetIconCacheTests
{
    [Fact]
    public void Now_playing_across_many_apps_keeps_only_a_few_icons()
    {
        var cfg = new WidgetConfig { Style = WidgetStyle.NowPlaying };
        for (int i = 0; i < 60; i++)
        {
            var data = WidgetSamples.Full();
            data.Activity!.Path = $@"C:\Games\Game{i}\game{i}.exe";
            data.Activity.Name = $"Game {i}";
            using var bmp = Rigsight.Agent.Widgets.WidgetRenderer.Render(cfg, data, 1, false, out _);
        }
        Assert.InRange(Rigsight.Agent.Widgets.WidgetRenderer.IconCache.Count, 1, Rigsight.Agent.Widgets.WidgetRenderer.IconCache.Capacity);
    }
}
