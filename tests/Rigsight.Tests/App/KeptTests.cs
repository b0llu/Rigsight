using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Rigsight.Core.Reports;
using Rigsight.Models;

namespace Rigsight.Tests.App;

/// <summary>Lists read again that say the same stay the ones the page has; one that differs changes only where it does.</summary>
public sealed class KeptTests
{
    private sealed record Row(string Name, int Value);

    [Fact]
    public void A_list_that_says_the_same_is_the_one_already_shown()
    {
        List<Row> shown = [new("a", 1), new("b", 2)];
        Assert.Same(shown, Kept.Or(shown, [new("a", 1), new("b", 2)]));
        List<Row> other = [new("a", 1), new("b", 3)], longer = [new("a", 1), new("b", 2), new("c", 3)], none = [];
        Assert.Same(other, Kept.Or(shown, other));
        Assert.Same(longer, Kept.Or(shown, longer));
        Assert.Same(none, Kept.Or(shown, none));
    }

    [Fact]
    public void Only_the_rows_that_differ_are_replaced()
    {
        var a = new Row("a", 1);
        var b = new Row("b", 2);
        var shown = new ObservableCollection<Row> { a, b, new("c", 3) };
        var changes = new List<NotifyCollectionChangedAction>();
        shown.CollectionChanged += (_, e) => changes.Add(e.Action);

        Kept.Sync(shown, [new("a", 1), new("b", 2), new("c", 3)]);
        Assert.Empty(changes);

        Kept.Sync(shown, [new("a", 1), new("b", 20), new("c", 3), new("d", 4)]);
        Assert.Same(a, shown[0]);
        Assert.Equal([new("a", 1), new("b", 20), new("c", 3), new("d", 4)], shown);
        Assert.Equal([NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add], changes);

        Kept.Sync(shown, [new("a", 1)]);
        Assert.Same(a, Assert.Single(shown));
    }

    [Fact]
    public void Rows_that_dont_compare_themselves_are_compared_by_all_they_hold()
    {
        AppStat Chrome(double active) => new() { Id = 7, Exe = "chrome.exe", Name = "Google Chrome", ActiveSec = active, MemMax = 900 };
        Assert.True(Kept.Values(Chrome(60), Chrome(60)));
        Assert.False(Kept.Values(Chrome(60), Chrome(120)));
        Assert.False(Kept.Values(Chrome(60), null));

        var kept = Chrome(60);
        var shown = new ObservableCollection<AppStat> { kept };
        Kept.Sync(shown, [Chrome(60)], Kept.Values);
        Assert.Same(kept, shown[0]);
        Kept.Sync(shown, [Chrome(61)], Kept.Values);
        Assert.NotSame(kept, shown[0]);
    }
}
