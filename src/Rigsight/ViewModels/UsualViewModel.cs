using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A row of Your usual as drawn: its icon in its colour, the name, the usual or what it still needs.</summary>
public sealed record UsualRowView(UsualItem Item, string Icon, string Brush)
{
    public string Name => Item.Name;
    public string? Value => Item.Value;
    public string? Detail => Item.Detail;
    public string? Tip => Item.Tip;
    public bool CanOpen => Item.Opens.Length > 0;
    public bool NeedsAdmin { get; init; }
}

public sealed record UsualSectionView(string Title, bool HasValues, IReadOnlyList<UsualRowView> Rows);

/// <summary>
/// Your usual, opened from Home's PC health card (issue #12's "how much has RigSight learned"): how much history the PC
/// has, then every usual it's compared with, or what each still needs. Read when opened, never kept: it's cheap enough,
/// and a day later the answers have moved. Closed with the x, Esc or a click outside; a row with an arrow opens its page.
/// </summary>
public sealed partial class UsualViewModel(ReportService reports, Func<bool> admin, Func<bool> canRestart, Action<string> goTo, Action<string> openGame, ICommand restartAdmin)
    : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _loading;

    public ObservableCollection<UsualTile> Tiles { get; } = [];
    public ObservableCollection<UsualSectionView> Sections { get; } = [];

    private int _opened;

    [RelayCommand]
    public async Task OpenAsync()
    {
        int turn = ++_opened;
        IsOpen = true;
        Loading = Sections.Count == 0;
        var overview = await reports.UsualOverviewAsync(admin());
        if (turn != _opened || !IsOpen) return;
        Loading = false;
        if (overview is null) return;
        Tiles.Clear();
        foreach (var t in overview.Tiles) Tiles.Add(t);
        Sections.Clear();
        bool restart = canRestart();
        foreach (var s in overview.Sections)
            Sections.Add(new(s.Title, s.HasValues, [.. s.Items.Select(i => new UsualRowView(i, IconOf(i.Kind), BrushOf(i.Kind)) { NeedsAdmin = i.NeedsAdmin && restart })]));
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void OpenRow(UsualRowView row)
    {
        if (!row.CanOpen) return;
        IsOpen = false;
        if (row.Item.Game is { Length: > 0 } game) openGame(game);
        else goTo(row.Item.Opens);
    }

    /// <summary>Restarts the agent with admin rights (the sidebar card's own command), for the rows not read without them.</summary>
    [RelayCommand]
    private void RestartAdmin()
    {
        IsOpen = false;
        if (restartAdmin.CanExecute(null)) restartAdmin.Execute(null);
    }

    internal static string IconOf(UsualKind kind) => kind switch
    {
        UsualKind.Rest or UsualKind.Cooling => "",
        UsualKind.Cpu => "",
        UsualKind.Game => "",
        UsualKind.Fan or UsualKind.FanWear => "",
        UsualKind.Problems => "",
        UsualKind.Power => "",
        UsualKind.Drive => "",
        UsualKind.Memory => "",
        UsualKind.Ping => "", // a gauge: the Network page's own icon is drawn, not a character
        _ => "",
    };

    /// <summary>The same colours Home's lines give the same things (see InsightBrushConverter).</summary>
    internal static string BrushOf(UsualKind kind) => kind switch
    {
        UsualKind.Game => ThemeManager.CategoryHex(Core.Settings.AppCategory.Game),
        UsualKind.Memory => ThemeManager.CategoryHex(Core.Settings.AppCategory.Media),
        UsualKind.Power => "MutedBrush",
        UsualKind.Drive => "SkyBrush",
        UsualKind.Ping => "GpuBrush",
        _ => "CpuBrush",
    };
}
