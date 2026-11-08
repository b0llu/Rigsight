using System.Drawing;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Readings in the taskbar's notification area (Settings: <c>TraySensors</c>, <c>TrayStyle</c>; the strip is TaskbarStrip): an icon per sensor,
/// or grouped by part, an icon for the CPU, one for the GPU…, each with up to two of its readings stacked. With tiles
/// (<c>TrayTiles</c>), every icon is a tile in its part's colour (or one picked), so the numbers say whose they are;
/// without, the numbers alone. Hovering shows the hardware, names and values; a click opens Rigsight; right-click
/// removes the icon's readings, switches between the two ways, or opens the Taskbar page. Separate from the Rigsight
/// icon, which keeps its own menu. UI thread only.
/// </summary>
internal sealed class TrayReadings(Action open, Action<IReadOnlyList<string>> remove, Action<bool> setGrouped, Action openPage) : IDisposable
{
    private sealed class Slot(TrayIcon icon) : IDisposable
    {
        public readonly TrayIcon Icon = icon;
        public string Key = "";                  // what's drawn now, so an unchanged reading isn't drawn again
        public string Tip = "";
        public ContextMenuStrip? Menu;
        public IReadOnlyList<TrayReading> Readings = [];

        public void Dispose()
        {
            Icon.Dispose();
            Menu?.Dispose();
        }
    }

    private readonly TrayIconHost _host = new();
    private readonly Dictionary<string, Slot> _slots = [];
    private bool _grouped;

    /// <summary>The readings each icon shows: one each, or grouped by part (see <see cref="TrayParts.Group"/>).</summary>
    internal static List<List<TrayReading>> Icons(IReadOnlyList<TrayReading> readings, bool grouped) =>
        grouped ? TrayParts.Group(readings, r => (r.Group, r.Kind)) : [.. readings.Select(r => new List<TrayReading> { r })];

    /// <summary>
    /// What an icon is, for Windows to know it by (see <see cref="TrayIcon"/>): the first reading it shows. The same
    /// in both ways of showing, so an icon dragged out from behind the arrow stays out when switching: a reading's
    /// icon becomes its part's icon (the part's first reading, the temperature as a rule), and back.
    /// </summary>
    internal static List<string> Keys(List<List<TrayReading>> icons) => [.. icons.Select(icon => "reading|" + icon[0].Id)];

    /// <summary>
    /// Shows <paramref name="readings"/> (none: no icons at all), in grayscale when <paramref name="gray"/>, each icon on
    /// a tile when <paramref name="tiles"/>, unless a reading has its own (see <see cref="Tile"/>). An icon that stays is kept as it is, so adding or removing one leaves the
    /// others where the user put them.
    /// </summary>
    public void Update(IReadOnlyList<TrayReading> readings, bool grouped, bool gray = false, bool tiles = false)
    {
        _grouped = grouped;
        var icons = Icons(readings, grouped);
        var keys = Keys(icons);
        foreach (var gone in _slots.Keys.Except(keys).ToList())
        {
            _slots[gone].Dispose();
            _slots.Remove(gone);
        }
        if (icons.Count == 0) return;

        bool light = WidgetRenderer.TaskbarIsLight();
        int size = SystemInformation.SmallIconSize.Width;
        for (int i = 0; i < icons.Count; i++)
        {
            if (!_slots.TryGetValue(keys[i], out var slot))
            {
                Slot? made = null;
                made = new Slot(new TrayIcon(_host, keys[i], button => Clicked(made!, button)));
                slot = _slots[keys[i]] = made;
            }
            slot.Readings = icons[i];
            Draw(slot, Lines(icons[i], light, gray), Tile(icons[i], light, gray, tiles), Tooltip(icons[i]), size, light);
        }
    }

    private void Clicked(Slot slot, MouseButtons button)
    {
        if (button == MouseButtons.Left) { open(); return; }
        slot.Menu ??= Menu(slot);
        slot.Menu.Show(Cursor.Position);
    }

    /// <summary>
    /// The icon's tile with tiles on: its first reading's (see <see cref="TrayReading.TileOn"/>); none while no reading is
    /// found, as the part isn't known, or with tiles off (the numbers alone). Tiles and grayscale are the first reading's
    /// own where it has them (<see cref="TrayReading.Tiles"/>, <see cref="TrayReading.Gray"/>), as on the strip; else the
    /// taskbar's. 2026-11-08: since 1.7.0 these icons stand in for the strip while the taskbar refuses it after sign-in,
    /// and readings set to no background came back on tiles, as only the taskbar's setting was looked at.
    /// </summary>
    internal static Color? Tile(IReadOnlyList<TrayReading> icon, bool light, bool gray, bool tiles) =>
        icon.Count == 0 || !(icon[0].Tiles ?? tiles) || icon.All(r => r.Hardware.Length == 0) ? null
            : icon[0].TileOn(light, icon[0].Gray ?? gray, icon: true);

    /// <summary>The icon's numbers, each in its colour (grayscale its own where it has it, else the taskbar's).</summary>
    internal static List<(string Text, Color Color)> Lines(IReadOnlyList<TrayReading> icon, bool light, bool gray) =>
        [.. icon.Take(TrayParts.PerIcon).Select(r => Line(r, light, r.Gray ?? gray))];

    /// <summary>
    /// Right-click: remove this icon's readings, switch between an icon each and grouped by part, or open the Taskbar
    /// page to pick others.
    /// </summary>
    private ContextMenuStrip Menu(Slot slot)
    {
        // Kept while the icon is (in either way of showing), so its words are set as it opens.
        var menu = DarkMenuRenderer.Create();
        var removeItem = new ToolStripMenuItem();
        removeItem.Click += (_, _) => { if (slot.Readings.Count > 0) remove([.. slot.Readings.Select(r => r.Id)]); };
        var switchItem = new ToolStripMenuItem();
        switchItem.Click += (_, _) => setGrouped(!_grouped);
        var pageItem = new ToolStripMenuItem();
        pageItem.Click += (_, _) => openPage();
        menu.Items.AddRange([removeItem, switchItem, new ToolStripSeparator(), pageItem]);
        menu.Opening += (_, _) =>
        {
            removeItem.Text = RemoveText(slot.Readings);
            switchItem.Text = _grouped ? Lang.T("Show an icon each") : Lang.T("Show an icon per part");
            pageItem.Text = Lang.T("Taskbar settings…");
        };
        return menu;
    }

    /// <summary>
    /// The remove item names what goes: one reading with its value, since sensors of one chip share names ("Remove GPU
    /// Core · 49 °C"); a grouped icon, its part's readings.
    /// </summary>
    internal static string RemoveText(IReadOnlyList<TrayReading> icon) => icon switch
    {
        [{ Name.Length: > 0 and <= 40 } r] => Lang.T($"Remove {r.Name} · {Units.Format(r.Kind, r.Value)}"),
        [_] or [] => Lang.T("Remove from the taskbar"),
        [var first, ..] when first.Hardware.Length > 0 => Lang.T($"Remove these {TrayParts.Label(first.Part)} readings"),
        _ => Lang.T("Remove these readings"),
    };

    private static (string Text, Color Color) Line(TrayReading r, bool light, bool gray) =>
        (WidgetRenderer.TrayText(r.Kind, r.Value), r.NumberOn(light, gray));

    private static void Draw(Slot slot, List<(string Text, Color Color)> lines, Color? tile, string tip, int size, bool light)
    {
        string key = $"{size}|{light}|{tile?.ToArgb()}|" + string.Join('|', lines.Select(l => $"{l.Text}:{l.Color.ToArgb()}"));
        if (key == slot.Key)
        {
            // The same numbers: only the hover text (it has the decimals) is new.
            if (tip != slot.Tip) slot.Icon.SetTip(tip);
            slot.Tip = tip;
            return;
        }
        slot.Key = key;
        slot.Tip = tip;
        using var bmp = WidgetRenderer.RenderTrayIcon(lines, size, tile: tile);
        slot.Icon.Set(bmp.GetHicon(), tip);
    }

    /// <summary>
    /// The hardware first, then each reading: "NVIDIA GeForce RTX 3080 Ti\nGPU Core: 49 °C\nGPU Core load: 35 %". Windows
    /// cuts tooltips at 127 characters: the hardware line goes first, then the names are cut.
    /// </summary>
    internal static string Tooltip(IReadOnlyList<TrayReading> icon)
    {
        var lines = icon.Select(r => $"{r.Name}: {Units.Format(r.Kind, r.Value)}").ToList();
        string hardware = icon.Select(r => r.Hardware).FirstOrDefault(h => h.Length > 0) ?? "";
        string full = string.Join('\n', hardware.Length > 0 ? lines.Prepend(hardware) : lines);
        return full.Length <= MaxTooltip ? full : Fit(string.Join('\n', lines));
    }

    private const int MaxTooltip = 127;

    private static string Fit(string text) => text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";

    public void Dispose()
    {
        foreach (var slot in _slots.Values) slot.Dispose();
        _slots.Clear();
        _host.Dispose();
    }
}
