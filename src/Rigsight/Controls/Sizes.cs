using System.Windows;

namespace Rigsight.Controls;

/// <summary>
/// Every text size in the app, in one place (2026-11-05, the user: "fix at all places so we can use from one point").
/// XAML uses them as <c>FontSize="{x:Static ctl:TextSize.Body}"</c>, code as <c>TextSize.Body</c>. Change a value here and
/// every place that shows that kind of text changes with it. Icons have their own sizes (<see cref="IconSize"/>), so
/// text can grow without the icons growing. Drawn pictures keep their own scale (FeaturePicture, the Rewind painter).
/// </summary>
public static class TextSize
{
    /// <summary>9: the smallest marks, a count on a badge.</summary>
    public const double Tiny = 9;
    /// <summary>10: small capital labels, chart axis words.</summary>
    public const double Mini = 10;
    /// <summary>10.5: key hints and small chips.</summary>
    public const double MiniPlus = 10.5;
    /// <summary>11: captions (the Caption style).</summary>
    public const double Caption = 11;
    /// <summary>11.5: short labels and hint lines under a setting.</summary>
    public const double Label = 11.5;
    /// <summary>12: small text, list details, options in a row of choices.</summary>
    public const double Small = 12;
    /// <summary>12.5: a secondary line under a title, row details.</summary>
    public const double Detail = 12.5;
    /// <summary>13: body text (the Body and Muted styles).</summary>
    public const double Body = 13;
    /// <summary>13.5: body text a little larger, tab names, list rows.</summary>
    public const double BodyPlus = 13.5;
    /// <summary>14: a setting's name, a lead line.</summary>
    public const double Lead = 14;
    /// <summary>15: a card's heading (the H2 style).</summary>
    public const double Heading = 15;
    /// <summary>16: a larger heading.</summary>
    public const double Subtitle = 16;
    /// <summary>17: a pop-up's heading.</summary>
    public const double SubtitlePlus = 17;
    /// <summary>18: a section title.</summary>
    public const double Title = 18;
    /// <summary>19: a dialog's title (Export, Instructions).</summary>
    public const double DialogTitle = 19;
    /// <summary>20: a figure on a tile (the TileValue style).</summary>
    public const double Figure = 20;
    /// <summary>22: a larger figure, a page section's title.</summary>
    public const double FigureLarge = 22;
    /// <summary>24: a big figure.</summary>
    public const double Display = 24;
    /// <summary>26: a page's title (the H1 style).</summary>
    public const double PageTitle = 26;
    /// <summary>28: a bigger figure.</summary>
    public const double DisplayLarge = 28;
    /// <summary>30: a headline figure.</summary>
    public const double Hero = 30;
    /// <summary>32: a larger headline figure.</summary>
    public const double HeroLarge = 32;
    /// <summary>34: the biggest figure on a card.</summary>
    public const double Jumbo = 34;
    /// <summary>36: a game's big numbers.</summary>
    public const double JumboLarge = 36;
    /// <summary>46: the number in the middle of a gauge (Memory, Temperatures).</summary>
    public const double Gauge = 46;
}

/// <summary>
/// Every icon size (Segoe Fluent Icons), by its size in pixels as Fluent names its icons: an icon is sized to the box it
/// sits in, so these stay put when <see cref="TextSize"/> changes.
/// </summary>
public static class IconSize
{
    public const double S8 = 8, S9 = 9, S10 = 10, S11 = 11, S12 = 12, S13 = 13, S14 = 14, S15 = 15, S16 = 16, S17 = 17,
        S18 = 18, S20 = 20, S24 = 24, S26 = 26, S28 = 28, S30 = 30, S34 = 34, S38 = 38, S40 = 40, S64 = 64;
}

/// <summary>
/// Every corner roundness, by what it rounds: <c>CornerRadius="{x:Static ctl:Corner.Card}"</c>. Pills and circles are not
/// here: their roundness is half their own height, so they stay round at any size. Neither are corners rounded on one
/// side only (a sheet's top, a bar's end).
/// </summary>
public static class Corner
{
    /// <summary>1: a hairline's ends.</summary>
    public static readonly CornerRadius Hairline = new(1);
    /// <summary>2: a thin bar or a page marker.</summary>
    public static readonly CornerRadius Bar = new(2);
    /// <summary>3: a small mark or a narrow bar.</summary>
    public static readonly CornerRadius Mark = new(3);
    /// <summary>4: a tag, a key hint.</summary>
    public static readonly CornerRadius Tag = new(4);
    /// <summary>5: a chip, a tick box.</summary>
    public static readonly CornerRadius Chip = new(5);
    /// <summary>6: a small box inside a control.</summary>
    public static readonly CornerRadius Small = new(6);
    /// <summary>7: an option inside a row of choices (inside <see cref="Control"/>, 3 px in) and small buttons.</summary>
    public static readonly CornerRadius Choice = new(7);
    /// <summary>8: a field, a menu, an icon box.</summary>
    public static readonly CornerRadius Field = new(8);
    /// <summary>9: a button.</summary>
    public static readonly CornerRadius Button = new(9);
    /// <summary>10: a row of choices, a sidebar item, a picker.</summary>
    public static readonly CornerRadius Control = new(10);
    /// <summary>12: a tile inside a card.</summary>
    public static readonly CornerRadius Tile = new(12);
    /// <summary>14: a panel, a pop-up card, a big icon tile.</summary>
    public static readonly CornerRadius Panel = new(14);
    /// <summary>16: a card.</summary>
    public static readonly CornerRadius Card = new(16);
    /// <summary>11: the keyboard focus ring around a control (its corner plus the ring's 4 px).</summary>
    public static readonly CornerRadius Focus = new(11);
    /// <summary>20: a large sheet (a Rewind card).</summary>
    public static readonly CornerRadius Sheet = new(20);
}
