using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>The tray icon's menu and its Widgets submenu, built as the agent builds them and clicked (never shown).</summary>
public class TrayMenuTests
{
    private sealed class Calls
    {
        public List<string> Log { get; } = [];
        public bool Paused { get; set; }
    }

    private static (ContextMenuStrip Menu, Calls Calls) Build(ToolStripMenuItem? widgets = null)
    {
        var calls = new Calls();
        var overlay = new ToolStripMenuItem("Show overlay");
        overlay.Click += (_, _) => calls.Log.Add("overlay");
        var menu = TrayController.BuildMenu(() => calls.Log.Add("open"), widgets ?? new ToolStripMenuItem("Widgets"), overlay,
            m => calls.Log.Add($"pause {m}"), () => calls.Log.Add("resume"), () => calls.Paused, () => calls.Log.Add("quit"));
        return (menu, calls);
    }

    private static void Opening(ContextMenuStrip menu) =>
        typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(menu, [new CancelEventArgs()]);

    private static void DropDownOpening(ToolStripMenuItem item) =>
        typeof(ToolStripDropDownItem).GetMethod("OnDropDownShow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, [EventArgs.Empty]);

    private static ToolStripMenuItem Find(ToolStripItemCollection items, string text) =>
        items.OfType<ToolStripMenuItem>().Single(i => i.Text == text);

    private static List<string> Shown(ContextMenuStrip menu) =>
        [.. menu.Items.Cast<ToolStripItem>().Where(i => i.Available).Select(i => i is ToolStripSeparator ? "-" : i.Text!)];

    [Fact]
    public void Every_item_does_what_it_says()
    {
        var (menu, calls) = Build();
        using (menu)
        {
            Opening(menu);
            Assert.Equal(["Open Rigsight", "Widgets", "Show overlay", "Pause tracking", "-", "Quit Rigsight"], Shown(menu));
            Find(menu.Items, "Open Rigsight").PerformClick();
            Find(menu.Items, "Show overlay").PerformClick();
            Find(menu.Items, "Quit Rigsight").PerformClick();
            Assert.Equal(["open", "overlay", "quit"], calls.Log);
        }
    }

    [Fact]
    public void Each_pause_choice_pauses_for_its_time()
    {
        var (menu, calls) = Build();
        using (menu)
        {
            var pause = Find(menu.Items, "Pause tracking");
            Assert.Equal(["For 30 minutes", "For 1 hour", "For 3 hours", "Until I resume"], pause.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text));
            foreach (ToolStripMenuItem choice in pause.DropDownItems) choice.PerformClick();
            Assert.Equal(["pause 30", "pause 60", "pause 180", "pause -1"], calls.Log);
        }
    }

    [Fact]
    public void While_paused_the_menu_offers_resume_instead()
    {
        var (menu, calls) = Build();
        using (menu)
        {
            calls.Paused = true;
            Opening(menu);
            Assert.Equal(["Open Rigsight", "Widgets", "Show overlay", "Resume tracking", "-", "Quit Rigsight"], Shown(menu));
            Find(menu.Items, "Resume tracking").PerformClick();
            Assert.Equal(["resume"], calls.Log);

            // Each time it opens it asks again.
            calls.Paused = false;
            Opening(menu);
            Assert.Contains("Pause tracking", Shown(menu));
            Assert.DoesNotContain("Resume tracking", Shown(menu));
        }
    }

    [Fact]
    public void Menus_follow_the_app_theme()
    {
        var before = DarkMenuRenderer.Theme;
        try
        {
            DarkMenuRenderer.Theme = "dark";
            Assert.Equal(System.Drawing.Color.FromArgb(22, 22, 22), DarkMenuRenderer.Bg);
            Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255), DarkMenuRenderer.TextColor);
            DarkMenuRenderer.Theme = "light";
            Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255), DarkMenuRenderer.Bg);
            Assert.Equal(System.Drawing.Color.FromArgb(0, 0, 0), DarkMenuRenderer.TextColor);
            DarkMenuRenderer.Theme = "system";
            Assert.Equal(WidgetRenderer.WindowsUsesLight(), DarkMenuRenderer.Light);

            // A menu's colours are read as it paints, so one built before the change follows it.
            using var menu = DarkMenuRenderer.Create();
            var table = ((ToolStripProfessionalRenderer)menu.Renderer).ColorTable;
            DarkMenuRenderer.Theme = "dark";
            Assert.Equal(System.Drawing.Color.FromArgb(22, 22, 22), table.ToolStripDropDownBackground);
            DarkMenuRenderer.Theme = "light";
            Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255), table.ToolStripDropDownBackground);
        }
        finally
        {
            DarkMenuRenderer.Theme = before;
        }
    }

    [Fact]
    public void The_widgets_submenu_turns_widgets_on_and_off_and_unlocks_them()
    {
        var settings = new RigsightSettings();
        foreach (var w in settings.Widgets) (w.Enabled, w.Locked) = (w.Style == WidgetStyle.Pill, true);
        int opened = 0;
        var widgets = new WidgetManager(() => settings, change => change(settings), () => opened++);
        var root = widgets.BuildTrayMenu();
        var (menu, _) = Build(root);
        using (menu)
        {
            DropDownOpening(root);
            var names = Enum.GetValues<WidgetStyle>().Select(WidgetManager.DisplayName).ToList();
            Assert.Equal([.. names, "", "Unlock all widgets", "Widget settings…"],
                root.DropDownItems.Cast<ToolStripItem>().Select(i => i is ToolStripSeparator ? "" : i.Text));
            Assert.Equal(["Slim bar"], root.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.Checked).Select(i => i.Text));

            Find(root.DropDownItems, "Gauges").PerformClick();
            Find(root.DropDownItems, "Slim bar").PerformClick();
            Assert.Equal([WidgetStyle.Gauges], settings.Widgets.Where(w => w.Enabled).Select(w => w.Style));

            // Opened again, it shows the new state.
            DropDownOpening(root);
            Assert.Equal(["Gauges"], root.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.Checked).Select(i => i.Text));

            Find(root.DropDownItems, "Unlock all widgets").PerformClick();
            Assert.All(settings.Widgets, w => Assert.False(w.Locked));
            Find(root.DropDownItems, "Widget settings…").PerformClick();
            Assert.Equal(1, opened);
        }
    }
}
