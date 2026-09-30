namespace Rigsight.Core.Settings;

/// <summary>How the app's sidebar is arranged (all by page key, e.g. "reports", "fans").</summary>
public sealed class SidebarSettings
{
    /// <summary>Pages in the order picked; pages not listed follow in their usual order.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>Pages left out of the sidebar (still reachable from links elsewhere).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Folded sections: "dashboards", "hardware", "onscreen".</summary>
    public List<string> Collapsed { get; set; } = [];
}
