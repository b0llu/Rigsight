using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A line of Your usual as drawn: a row (name and usual, grey when not there yet), a need line, or a sentence.</summary>
public sealed record UsualLineView(UsualItem Item)
{
    public bool IsRow => Item.Line == UsualLine.Row;
    public bool IsNeed => Item.Line == UsualLine.Need;
    public bool IsNote => Item.Line == UsualLine.Note;
    public string Text => Item.Text;
    public string? Value => Item.Value;
    public bool Dim => Item.Dim;
    public string? Tip => Item.Tip;
    public bool CanOpen => Item.Opens.Length > 0;
    /// <summary>Offers the restart with admin rights (not on a standard account, which can't give them).</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>A part of Your usual: its heading with its icon, and whether a line sets it apart from the one above.</summary>
public sealed record UsualSectionView(string Title, string Icon, string Brush, bool First, IReadOnlyList<UsualLineView> Lines);

/// <summary>
/// Your usual, opened from Home's PC health card (issue #12's "how much has RigSight learned"): a panel at the right
/// with how much history the PC has, then every usual it's compared with, or what each still needs. Closed with the x,
/// Esc or a click on the page; a row with an arrow opens its page.
/// Opening felt laggy (the user, 2026-11-20: ~650 ms from the click to the lines, of which the read was ~30 ms): the
/// panel was built anew on every open, faded in empty, then filled one part at a time. Now it's built once and kept
/// (<see cref="Built"/>), shows the last answers at once, is filled in one go, and is read ahead when the pointer
/// reaches the link (<see cref="Prefetch"/>); answers older than <see cref="FreshFor"/> are read again behind it.
/// </summary>
public sealed partial class UsualViewModel(ReportService reports, Func<bool> admin, Func<bool> canRestart, Action<string> goTo, Action<string> openGame, ICommand restartAdmin)
    : ObservableObject
{
    /// <summary>How long answers are shown without reading them again (the usuals move by the day, not the second).</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(1);

    [ObservableProperty] private bool _isOpen;
    /// <summary>Opened at least once: the panel is built then and kept, not built again on every open.</summary>
    [ObservableProperty] private bool _built;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _since = "";
    [ObservableProperty] private string _facts = "";
    /// <summary>Set whole, once per read: one layout, not one per part.</summary>
    [ObservableProperty] private IReadOnlyList<UsualSectionView> _sections = [];

    private DateTime _readAt = DateTime.MinValue;
    private Task? _reading;

    /// <summary>Reads ahead (the pointer is on the link), unless fresh answers are there or a read is under way.</summary>
    public void Prefetch()
    {
        if (DateTime.Now - _readAt < FreshFor || _reading is { IsCompleted: false }) return;
        _reading = ReadAsync();
    }

    [RelayCommand]
    public async Task OpenAsync()
    {
        Built = true;
        IsOpen = true;
        Loading = Sections.Count == 0;
        Prefetch();
        if (_reading is { } reading) await reading;
        Loading = false;
    }

    private async Task ReadAsync()
    {
        var overview = await reports.UsualOverviewAsync(admin());
        if (overview is null) return;
        _readAt = DateTime.Now;
        bool restart = canRestart();
        Since = overview.Since;
        Facts = overview.Facts;
        Sections = [.. overview.Sections.Select((s, i) => new UsualSectionView(s.Title, IconOf(s.Kind), BrushOf(s.Kind), i == 0,
            [.. s.Items.Select(item => new UsualLineView(item) { NeedsAdmin = item.NeedsAdmin && restart })]))];
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void OpenRow(UsualLineView line)
    {
        if (!line.CanOpen) return;
        IsOpen = false;
        if (line.Item.Game is { Length: > 0 } game) openGame(game);
        else goTo(line.Item.Opens);
    }

    /// <summary>Restarts the agent with admin rights (the sidebar card's own command), for what isn't read without them.</summary>
    [RelayCommand]
    private void RestartAdmin()
    {
        IsOpen = false;
        _readAt = DateTime.MinValue; // read again next time: the rows without admin rights change
        if (restartAdmin.CanExecute(null)) restartAdmin.Execute(null);
    }

    /// <summary>The heading's icon: the same as the page's in the sidebar where it has a character of its own.</summary>
    internal static string IconOf(UsualSectionKind kind) => kind switch
    {
        UsualSectionKind.Temperatures => "",
        UsualSectionKind.Fans => "",
        UsualSectionKind.Problems => "",
        UsualSectionKind.Months => "",
        _ => "",
    };

    /// <summary>The same colours Home's lines give the same things (see InsightBrushConverter).</summary>
    internal static string BrushOf(UsualSectionKind kind) => kind == UsualSectionKind.Apps
        ? ThemeManager.CategoryHex(Core.Settings.AppCategory.Media) : "CpuBrush";
}
