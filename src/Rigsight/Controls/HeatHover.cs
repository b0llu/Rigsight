using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// What a temperature hover says was on screen (2026-11-06, the user: "show which app was active so the user will
/// easily know why temps were high, like you show on Network"): the app in front, in the Network hover's words, under
/// the time in the Reports Temperatures card's hover and the Temperatures page's. (Its list of the apps using the CPU
/// and GPU most was taken out the same day, at the user's word.)
/// </summary>
/// <param name="Front">The apps longest in front, longest first (one for a minute).</param>
/// <param name="Timed">Whether the front apps are said with how long (an hour, a day), not for a minute.</param>
public sealed record HeatWho(IReadOnlyList<FrontApp> Front, bool Timed = true)
{
    public bool IsEmpty => Front.Count == 0;

    /// <summary>
    /// The line under the time, in the Network hover's words where a game was played: "Playing Phantom Blade Zero for
    /// 44m"; else "Claude Code in front for 18m, Google Chrome for 17m"; for a minute, without the times.
    /// </summary>
    public string? FrontWords
    {
        get
        {
            if (Front.Count == 0) return null;
            var a = Front[0];
            string time = Units.Duration(a.Sec);
            string first = a.IsGame
                ? Timed ? Lang.T($"Playing {a.Name} for {time}") : Lang.T($"Playing {a.Name}")
                : Timed ? Lang.T($"{a.Name} in front for {time}") : Lang.T($"{a.Name} in front");
            if (!Timed || Front.Count < 2) return first;
            var b = Front[1];
            string second = Units.Duration(b.Sec);
            return first + ", " + Lang.T($"{b.Name} for {second}");
        }
    }
}

internal static class HeatHover
{
    private const double Icon = 16, Gap = 8, LineH = 22;

    /// <summary>
    /// The least a temperature box is across, the same wherever the pointer is: the line of what was in front is cut
    /// to it, never widening the box (2026-11-06: "the hover popup causes layout shift").
    /// </summary>
    public const double BoxWidth = 300;

    /// <summary>The line's height with the gap under it: kept whether or not there's an app to name.</summary>
    public const double RowHeight = LineH + 2;

    /// <summary>What the line says when nothing was in front and in use (the screen locked, nobody at the PC).</summary>
    public static string NoApp => Lang.T("No app in use");

    /// <summary>
    /// The line of what was in front, with its app's icon, at <paramref name="y"/>; with no app to name, the line is
    /// still there, saying <paramref name="instead"/> (or <see cref="NoApp"/>), so the box keeps its size. Returns the
    /// height used (<see cref="RowHeight"/>).
    /// </summary>
    public static double DrawFront(DrawingContext dc, Visual v, HeatWho? who, double x, double y, double width, string? instead = null)
    {
        if (who?.FrontWords is not { } words)
        {
            var none = ChartPaint.Format(v, instead ?? NoApp, 12, ChartPaint.Muted);
            none.MaxTextWidth = Math.Max(10, width);
            none.MaxLineCount = 1;
            none.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(none, new Point(x, y + (LineH - none.Height) / 2));
            return RowHeight;
        }
        DrawIcon(dc, v, who.Front[0].Path, who.Front[0].Name, x, y + (LineH - Icon) / 2);
        var ft = ChartPaint.Format(v, words, 12, ChartPaint.TextBrush);
        ft.MaxTextWidth = Math.Max(10, width - Icon - Gap);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(ft, new Point(x + Icon + Gap, y + (LineH - ft.Height) / 2));
        return LineH + 2;
    }

    /// <summary>The app's own icon, or its first letter on a small tile when it has none.</summary>
    private static void DrawIcon(DrawingContext dc, Visual v, string? path, string name, double x, double y)
    {
        var rect = new Rect(x, y, Icon, Icon);
        if (Services.IconCache.Get(path) is { } icon)
        {
            dc.DrawImage(icon, rect);
            return;
        }
        dc.DrawRoundedRectangle(ChartPaint.Track, null, rect, 4, 4);
        string letter = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        ChartPaint.Text(dc, v, letter, new Point(x + Icon / 2, y + Icon / 2), 10, ChartPaint.Muted, ChartPaint.Align.Center, bold: true);
    }
}
