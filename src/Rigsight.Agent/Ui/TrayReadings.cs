using System.Drawing;
using Rigsight.Agent.Widgets;
using Rigsight.Core;

namespace Rigsight.Agent.Ui;

/// <summary>
/// A sensor for the taskbar, as read on the sampler thread: its name for the tooltip, its reading, the part it's
/// about (for its colour mark and grouping) with that hardware's name, and the word under it on the strip
/// (<see cref="TrayParts.ShortLabel"/>).
/// </summary>
internal readonly record struct TrayReading(string Id, string Name, SensorKind Kind, double? Value, TrayPart Part, string Hardware, string Short = "")
{
    /// <summary>Readings with the same group share icons when grouped (see <see cref="TrayParts.GroupOf"/>).</summary>
    public string Group => TrayParts.GroupOf(Part, Id);
}

/// <summary>
/// Readings in the taskbar's notification area (Settings: <c>TraySensors</c>, <c>TrayStyle</c>; the strip is TaskbarStrip): an icon per sensor,
/// or grouped by part, an icon for the CPU, one for the GPU…, each with up to two of its readings stacked. Every icon
/// carries its part's colour mark, so the numbers say whose they are. Hovering shows the hardware, names and values; a
/// click opens Rigsight; right-click removes the icon's readings, switches between the two ways, or opens the Taskbar
/// page. Separate from the Rigsight icon, which keeps its own menu. UI thread only.
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
    /// Shows <paramref name="readings"/> (none: no icons at all). An icon that stays is kept as it is, so adding or
    /// removing one leaves the others where the user put them.
    /// </summary>
    public void Update(IReadOnlyList<TrayReading> readings, bool grouped)
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
            Draw(slot, [.. icons[i].Select(r => Line(r, light))], Mark(icons[i], light), Tooltip(icons[i]), size, light);
        }
    }

    private void Clicked(Slot slot, MouseButtons button)
    {
        if (button == MouseButtons.Left) { open(); return; }
        slot.Menu ??= Menu(slot);
        slot.Menu.Show(Cursor.Position);
    }

    /// <summary>The icon's colour mark: its part's (none while a reading isn't found, as the part isn't known).</summary>
    private static Color? Mark(IReadOnlyList<TrayReading> icon, bool light) =>
        icon.All(r => r.Hardware.Length == 0) ? null : WidgetRenderer.TrayMark(icon[0].Part, light);

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
        var pageItem = new ToolStripMenuItem("Taskbar settings…");
        pageItem.Click += (_, _) => openPage();
        menu.Items.AddRange([removeItem, switchItem, new ToolStripSeparator(), pageItem]);
        menu.Opening += (_, _) =>
        {
            removeItem.Text = RemoveText(slot.Readings);
            switchItem.Text = _grouped ? "Show an icon each" : "Show an icon per part";
        };
        return menu;
    }

    /// <summary>
    /// The remove item names what goes: one reading with its value, since sensors of one chip share names ("Remove GPU
    /// Core · 49 °C"); a grouped icon, its part's readings.
    /// </summary>
    internal static string RemoveText(IReadOnlyList<TrayReading> icon) => icon switch
    {
        [{ Name.Length: > 0 and <= 40 } r] => $"Remove {r.Name} · {Units.Format(r.Kind, r.Value)}",
        [_] or [] => "Remove from the taskbar",
        [var first, ..] when first.Hardware.Length > 0 => $"Remove these {TrayParts.Label(first.Part)} readings",
        _ => "Remove these readings",
    };

    private static (string Text, Color Color) Line(TrayReading r, bool light) =>
        (WidgetRenderer.TrayText(r.Kind, r.Value), WidgetRenderer.TrayColor(r.Kind, r.Value, light));

    private static void Draw(Slot slot, List<(string Text, Color Color)> lines, Color? mark, string tip, int size, bool light)
    {
        string key = $"{size}|{light}|{mark?.ToArgb()}|" + string.Join('|', lines.Select(l => $"{l.Text}:{l.Color.ToArgb()}"));
        if (key == slot.Key)
        {
            // The same numbers: only the hover text (it has the decimals) is new.
            if (tip != slot.Tip) slot.Icon.SetTip(tip);
            slot.Tip = tip;
            return;
        }
        slot.Key = key;
        slot.Tip = tip;
        using var bmp = WidgetRenderer.RenderTrayIcon(lines, size, mark);
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
