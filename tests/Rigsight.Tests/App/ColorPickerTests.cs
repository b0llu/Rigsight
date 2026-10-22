using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>A taskbar reading's colour picker: quick picks used at once, "+" for any colour, Default back to RigSight's.</summary>
public sealed class ColorPickerTests
{
    [Fact]
    public void A_pick_is_used_at_once_and_the_custom_colour_opens_from_the_plus()
    {
        var picks = new List<(string? Number, string? Name)>();
        Ui.Run(() =>
        {
            var target = new Button { Content = "Colors" };
            var picker = new ColorPicker();
            var window = new Window
            {
                Left = -32000, Top = -32000, Width = 400, Height = 200, ShowActivated = false, ShowInTaskbar = false,
                Content = new StackPanel { Children = { target, picker } },
            };
            window.Show();
            picker.Changed += (n, m) => picks.Add((n, m));
            var made = new List<(string Kind, string Hex)>();
            picker.CustomMade += (kind, hex) => made.Add((kind, hex));
            picker.Open(target, "CPU Total", temperature: false, number: null, name: null, "#5B8CFF", "Default puts back RigSight's colors.", [], ["#123456"]);

            var card = (FrameworkElement)picker.Pop.Child;
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();

            // Not a temperature: no "By heat". The name shows its part's colour (blue) as the one in use.
            Assert.Equal(Visibility.Collapsed, picker.HeatButton.Visibility);
            var nameTiles = picker.NameTiles.Children.OfType<Button>().ToList();
            Assert.Equal("on", nameTiles.Single(b => (string)b.ToolTip == "Blue").Tag);

            Click(picker.NumberTiles.Children.OfType<Button>().Single(b => (string)b.ToolTip == "Magenta"));
            Assert.Equal(("#E879F9", (string?)null), picks[^1]);

            // "+" on the name's row opens the custom colour, starting from the colour in use; Use applies it.
            Click(picker.NameTiles.Children.OfType<Button>().Last());
            card.UpdateLayout();
            Assert.Equal(Visibility.Visible, picker.Custom.Visibility);
            picker.Code.Text = "#FF6A00";
            Click(picker.UseButton);
            Assert.Equal(("#E879F9", "#FF6A00"), picks[^1]);
            // The new colour is kept for the name's row only, after the one made before and just before "+".
            Assert.Equal([("name", "#FF6A00")], made);
            var nameRow = picker.NameTiles.Children.OfType<Button>().Select(b => b.ToolTip as string).ToList();
            Assert.Equal(["Custom color #123456", "Custom color #FF6A00", "Custom color"], nameRow[^3..]);
            Assert.Equal("on", picker.NameTiles.Children.OfType<Button>().Single(b => (string)b.ToolTip == "Custom color #FF6A00").Tag);
            Assert.DoesNotContain(picker.NumberTiles.Children.OfType<Button>(), b => ((string)b.ToolTip).StartsWith("Custom color #"));

            // Past three rows the tiles scroll, with the fourth half in sight.
            foreach (var hex in new[] { "#010101", "#020202", "#030303", "#040404", "#050505" }) { picker.Code.Text = hex; Click(picker.UseButton); }
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            card.Arrange(new Rect(card.DesiredSize));
            card.UpdateLayout();
            Assert.Equal(140, picker.NameScroll.ActualHeight, 1);
            Assert.True(picker.NameScroll.ScrollableHeight > 0);

            card.UpdateLayout();
            Ui.SavePng(card, Path.Combine(TestEnvironment.DataDir, "screens", "color-picker.png"));

            Click(Visuals.Descendants<Button>(card).Single(b => b.Content as string == "Default"));
            Assert.Equal(((string?)null, (string?)null), picks[^1]);
            window.Close();
        });
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
