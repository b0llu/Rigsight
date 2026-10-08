using System.Windows.Controls;
using System.Windows.Data;
using Rigsight.Controls;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>A search box waits for a pause in the typing before the page hears of it, and says it's about to search.</summary>
[Collection("UI")]
public sealed class DebounceTests
{
    private sealed class Page
    {
        public List<string> Heard { get; } = [];
        private string _search = "";
        public string Search { get => _search; set { _search = value; Heard.Add(value); } }
    }

    private static (TextBox Box, Page Page) Box() => Ui.Run(() =>
    {
        var page = new Page();
        var box = new TextBox { DataContext = page };
        box.SetBinding(Debounce.TextProperty, new Binding(nameof(Page.Search)));
        return (box, page);
    });

    [Fact]
    public void Typing_is_passed_on_once_after_a_pause_not_at_every_letter()
    {
        var (box, page) = Box();
        Ui.Run(() =>
        {
            foreach (string typed in new[] { "d", "di", "dis", "disc" }) box.Text = typed;
            Assert.Empty(page.Heard);
            Assert.True(Debounce.GetIsPending(box));
        });
        Assert.True(Ui.WaitFor(() => page.Heard.Count > 0, 3000));
        Ui.Run(() =>
        {
            Assert.Equal(["disc"], page.Heard);
            Assert.False(Debounce.GetIsPending(box));
        });
    }

    [Fact]
    public void Clearing_the_box_is_passed_on_at_once_and_the_pages_own_change_shows_in_the_box()
    {
        var (box, page) = Box();
        Ui.Run(() => box.Text = "discord");
        Assert.True(Ui.WaitFor(() => page.Heard.Count > 0, 3000));
        Ui.Run(() =>
        {
            box.Text = "";
            Assert.Equal(["discord", ""], page.Heard);
            Assert.False(Debounce.GetIsPending(box));

            // The page sets it (a link opened with something to look for): shown, with nothing to wait for.
            Debounce.SetText(box, "nvidia");
            Assert.Equal("nvidia", box.Text);
            Assert.False(Debounce.GetIsPending(box));
        });
    }
}
