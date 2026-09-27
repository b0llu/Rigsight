using System.Drawing;
using System.Runtime.InteropServices;
using Rigsight.Agent.Widgets;
using Rigsight.Core;

namespace Rigsight.Agent.Ui;

/// <summary>A sensor for the taskbar, as read on the sampler thread: its name for the tooltip, and its reading.</summary>
internal readonly record struct TrayReading(string Id, string Name, SensorKind Kind, double? Value);

/// <summary>
/// Readings in the taskbar's notification area (Settings: <c>TraySensors</c>, <c>TrayCombined</c>): an icon per sensor,
/// or one icon for all of them showing two at a time, stacked, taking turns every few seconds. Hovering shows the names
/// and values; a click opens Rigsight; right-click removes a reading, switches between the two, or opens the Taskbar
/// page. Separate from the Rigsight icon, which keeps its own menu. UI thread only.
/// </summary>
internal sealed class TrayReadings(Action open, Action<string> remove, Action removeAll, Action<bool> setCombined, Action openPage) : IDisposable
{
    /// <summary>How long the combined icon shows each pair before the next.</summary>
    public const int TurnMs = 3000;

    private sealed class Slot : IDisposable
    {
        public readonly NotifyIcon Icon = new();
        public string Key = "";          // what's drawn now, so an unchanged reading isn't drawn again
        public TrayReading? Reading;     // the one this icon shows (null: the combined icon)
        private IntPtr _handle;

        public void Show(Bitmap bmp)
        {
            var old = _handle;
            _handle = bmp.GetHicon();
            var previous = Icon.Icon;
            Icon.Icon = System.Drawing.Icon.FromHandle(_handle);
            previous?.Dispose();
            if (old != IntPtr.Zero) DestroyIcon(old);
        }

        public void Dispose()
        {
            Icon.Visible = false;
            Icon.ContextMenuStrip?.Dispose();
            Icon.Icon?.Dispose();
            Icon.Dispose();
            if (_handle != IntPtr.Zero) DestroyIcon(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private readonly List<Slot> _slots = [];
    private string _layout = "";

    /// <summary>Shows <paramref name="readings"/> (none: no icons at all).</summary>
    public void Update(IReadOnlyList<TrayReading> readings, bool combined)
    {
        int wanted = readings.Count == 0 ? 0 : combined ? 1 : readings.Count;
        // Icons are only made or removed when the set changes, so they keep their place in the taskbar.
        string layout = (combined ? "1|" : "n|") + string.Join('|', readings.Select(r => r.Id));
        if (layout != _layout || _slots.Count != wanted)
        {
            _layout = layout;
            foreach (var slot in _slots) slot.Dispose();
            _slots.Clear();
            for (int i = 0; i < wanted; i++)
            {
                var slot = new Slot();
                slot.Icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
                slot.Icon.ContextMenuStrip = Menu(slot, combined);
                _slots.Add(slot);
            }
        }
        if (wanted == 0) return;

        bool light = WidgetRenderer.TaskbarIsLight();
        int size = SystemInformation.SmallIconSize.Width;
        if (combined)
        {
            var pair = Turn(readings, Environment.TickCount64);
            Draw(_slots[0], [.. pair.Select(r => Line(r, light))], size, light);
            _slots[0].Icon.Text = CombinedTooltip(readings);
        }
        else
        {
            for (int i = 0; i < readings.Count; i++)
            {
                _slots[i].Reading = readings[i];
                Draw(_slots[i], [Line(readings[i], light)], size, light);
                _slots[i].Icon.Text = Tooltip(readings[i]);
            }
        }
        // Where each icon goes is Windows' choice (it remembers where the user dragged them), not the order here.
        foreach (var slot in _slots) slot.Icon.Visible = true;
    }

    /// <summary>
    /// Right-click: remove this reading (all of them, on the combined icon), switch between an icon each and all in one,
    /// or open the Taskbar page to pick others.
    /// </summary>
    private ContextMenuStrip Menu(Slot slot, bool combined)
    {
        var menu = DarkMenuRenderer.Create();
        var removeItem = new ToolStripMenuItem();
        removeItem.Click += (_, _) =>
        {
            if (combined) removeAll();
            else if (slot.Reading is { } r) remove(r.Id);
        };
        var switchItem = new ToolStripMenuItem(combined ? "Show an icon each" : "Show all in one icon");
        switchItem.Click += (_, _) => setCombined(!combined);
        var pageItem = new ToolStripMenuItem("Taskbar settings…");
        pageItem.Click += (_, _) => openPage();
        menu.Items.AddRange([removeItem, switchItem, new ToolStripSeparator(), pageItem]);
        menu.Opening += (_, _) => removeItem.Text = RemoveText(combined, slot.Reading);
        return menu;
    }

    /// <summary>
    /// The remove item names what goes, with its reading, since sensors of one chip share names ("Remove GPU Core · 49 °C");
    /// on the combined icon, all of them.
    /// </summary>
    internal static string RemoveText(bool combined, TrayReading? reading) =>
        combined ? "Remove all from the taskbar"
        : reading is { Name.Length: > 0 and <= 40 } r ? $"Remove {r.Name} · {Units.Format(r.Kind, r.Value)}"
        : "Remove from the taskbar";

    private static (string Text, Color Color) Line(TrayReading r, bool light) =>
        (WidgetRenderer.TrayText(r.Kind, r.Value), WidgetRenderer.TrayColor(r.Kind, r.Value, light));

    private static void Draw(Slot slot, List<(string Text, Color Color)> lines, int size, bool light)
    {
        string key = $"{size}|{light}|" + string.Join('|', lines.Select(l => $"{l.Text}:{l.Color.ToArgb()}"));
        if (key == slot.Key) return;
        slot.Key = key;
        using var bmp = WidgetRenderer.RenderTrayIcon(lines, size);
        slot.Show(bmp);
    }

    /// <summary>The pair the combined icon shows at <paramref name="nowMs"/>: two at a time, in order, <see cref="TurnMs"/> each.</summary>
    internal static IReadOnlyList<TrayReading> Turn(IReadOnlyList<TrayReading> readings, long nowMs)
    {
        if (readings.Count <= 2) return readings;
        int pairs = (readings.Count + 1) / 2;
        int pair = (int)(nowMs / TurnMs % pairs);
        return [.. readings.Skip(pair * 2).Take(2)];
    }

    /// <summary>"CPU Package: 62 °C" (Windows cuts tooltips at 127 characters).</summary>
    internal static string Tooltip(TrayReading r) => Fit($"{r.Name}: {Units.Format(r.Kind, r.Value)}");

    /// <summary>Every reading on its own line; if they don't all fit Windows' 127 characters, the last line says how many more.</summary>
    internal static string CombinedTooltip(IReadOnlyList<TrayReading> readings)
    {
        var lines = readings.Select(r => $"{r.Name}: {Units.Format(r.Kind, r.Value)}").ToList();
        for (int shown = lines.Count; shown > 0; shown--)
        {
            string text = string.Join('\n', lines.Take(shown));
            if (shown < lines.Count) text += $"\n…and {lines.Count - shown} more";
            if (text.Length <= MaxTooltip) return text;
        }
        return Fit(lines[0]);
    }

    private const int MaxTooltip = 127;

    private static string Fit(string text) => text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";

    public void Dispose()
    {
        foreach (var slot in _slots) slot.Dispose();
        _slots.Clear();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
