using System.Drawing;
using Rigsight.Core;
using Rigsight.Core.Settings;
#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
using Rigsight.Agent.Widgets;

namespace Rigsight.Agent.Ui;
#endif

/// <summary>
/// A sensor for the taskbar, as read on the sampler thread: its name for the tooltip, its reading, the part it's
/// about (for its colour mark and grouping) with that hardware's name, and the word under it on the strip
/// (<see cref="TrayParts.ShortLabel"/>; <paramref name="OwnShort"/>: the short name given on All sensors), the colours picked
/// for it on the Taskbar page (see <see cref="TrayColors"/>), and whether it has a box of its own in two rows.
/// Since 1.3 also its own <paramref name="Style"/> and <paramref name="Size"/>; the reading its look is drawn from
/// (<paramref name="LookId"/>, its kind and value: another reading's for "Together", else its own); a second value
/// (<paramref name="Second"/>: the upload speed of both speeds); and its cores' loads, 0 to 1, for Each core.
/// Since 1.3 also its own colours or grayscale (<paramref name="Gray"/>), background (<paramref name="Tiles"/>), names as
/// one letter in two rows (<paramref name="Letters"/>), and whether it shares its spot with the next (<paramref name="PairNext"/>;
/// null while no pairs are saved: as before 1.3), each already its own or the taskbar's (null: the taskbar's, as the drawing is told).
/// Shared with the Taskbar page, which draws its preview with the agent's own drawing (linked into the app).
/// </summary>
internal readonly record struct TrayReading(string Id, string Name, SensorKind Kind, double? Value, TrayPart Part, string Hardware, string Short = "",
    string? NumberColor = null, string? NameColor = null, TrayLook Look = TrayLook.Number, string? TileColor = null, bool OwnShort = false, bool Alone = false,
    TrayStyle Style = TrayStyle.Strip, TraySize Size = TraySize.Auto, string? LookId = null, SensorKind? LookKind = null, double? LookValue = null,
    double? Second = null, float[]? Cores = null, bool? Gray = null, bool? Tiles = null, bool? Letters = null, bool? PairNext = null, bool GraphBehind = false)
{
    /// <summary>What its spot depends on (see <see cref="TrayParts.Spots"/>).</summary>
    public TrayParts.SpotOf Spot => new(Id, Group, Kind, Alone, BothSpeeds, PairNext);

    /// <summary>Both speeds in one reading (download its number, upload with it).</summary>
    public bool BothSpeeds => Id == TrayParts.NetBoth;

    /// <summary>The reading its look shows: another's when it has one (Together), else its own.</summary>
    public string LookSource => LookId ?? Id;

    /// <summary>The kind its look shows.</summary>
    public SensorKind LookSourceKind => LookKind ?? Kind;

    /// <summary>The value its look shows (how full a ring is, the graph's newest point).</summary>
    public double? LookSourceValue => LookId is null ? Value : LookValue;

    /// <summary>
    /// How it draws on the strip: its look; a filled graph in place of a fill-up look it can't have, and Each core only
    /// with the cores known (see <see cref="TrayParts.LookFor"/>).
    /// </summary>
    public TrayLook StripLook
    {
        get
        {
            var look = TrayParts.LookFor(LookSourceKind, Look, BothSpeeds);
            return look == TrayLook.Cores && Cores is not { Length: > 0 } ? TrayLook.Blocks : look;
        }
    }

    /// <summary>How it draws on an icon: its number, the icon filling up (Level), or a ring round it (see <see cref="TrayParts.IconLook"/>).</summary>
    public TrayLook IconLook => TrayParts.IconLook(LookSourceKind, Look);

    /// <summary>How full its look is, 0 to 1 (see <see cref="TrayParts.Fill"/>).</summary>
    public float Fill => TrayParts.Fill(LookSourceValue);

    /// <summary>
    /// Its tile's colour (see <see cref="TrayColors.TileAlpha"/>): the one picked, soft; else, on an icon, its name's
    /// colour, fainter; else the strip's faint neutral tile. Grayscale: grey tiles, whatever was picked.
    /// </summary>
    public Color TileOn(bool light, bool gray, bool icon)
    {
        if (!gray && TrayColors.Parse(TileColor) is { } c) return Color.FromArgb(TrayColors.TileAlpha(true, light), Of(c));
        if (icon) return Color.FromArgb(TrayColors.TileAlpha(false, light), NameOn(light, gray));
        return Color.FromArgb(TrayColors.NeutralTileAlpha(light), light ? Color.Black : Color.White);
    }

    /// <summary>Readings with the same group share icons when grouped (see <see cref="TrayParts.GroupOf"/>).</summary>
    public string Group => TrayParts.GroupOf(Part, Id);

    /// <summary>
    /// The number's colour: the one picked (made readable on this taskbar, 4.5:1), else as RigSight colours it (a
    /// temperature by how warm it is, the rest in the taskbar's text colour). Grayscale is grey whatever was picked.
    /// </summary>
    public Color NumberOn(bool light, bool gray) =>
        !gray && TrayColors.Parse(NumberColor) is { } c ? Of(TrayColors.ForTaskbar(c, light, TrayColors.TextContrast)) : WidgetRenderer.TrayColor(Kind, Value, light, gray);

    /// <summary>The name's colour, which its mark and graph take too: the one picked, else its part's.</summary>
    public Color NameOn(bool light, bool gray) =>
        !gray && TrayColors.Parse(NameColor) is { } c ? Of(TrayColors.ForTaskbar(c, light)) : WidgetRenderer.TrayMark(Part, light, gray);

    /// <summary>
    /// The colour its words are written in ("CPU", "Load"): its name's, deep enough to read at 4.5:1 on the bar (a light
    /// taskbar gets a deeper shade of the part's colour, see <see cref="TrayParts.WordColor"/>; a picked one is deepened).
    /// </summary>
    public Color WordOn(bool light, bool gray) =>
        !gray && TrayColors.Parse(NameColor) is { } c ? Of(TrayColors.ForTaskbar(c, light, TrayColors.TextContrast)) : WidgetRenderer.TrayWord(Part, light, gray);

    private static Color Of((byte R, byte G, byte B) c) => Color.FromArgb(c.R, c.G, c.B);
}
