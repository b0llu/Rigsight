namespace Rigsight.Core.Settings;

/// <summary>How the app's sidebar is arranged (all by page key, e.g. "reports", "fans"; a dashboard's is "custom:" and its ID).</summary>
public sealed class SidebarSettings
{
    /// <summary>Pages in the order picked; pages not listed follow in their usual order, at the end of their group.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>Pages left out of the sidebar (still reachable from links elsewhere).</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Folded sections: "dashboards", "hardware", "onscreen".</summary>
    public List<string> Collapsed { get; set; } = [];

    /// <summary>Pages moved out of their own group, with the group they're in now: "main", "dashboards", "hardware" or "onscreen".</summary>
    public Dictionary<string, string> Groups { get; set; } = [];

    /// <summary>Pages given another name, as typed.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>Headings given another name, by section ("dashboards", "hardware", "onscreen"), as typed.</summary>
    public Dictionary<string, string> HeadingNames { get; set; } = [];

    /// <summary>Sections whose heading is left out: their pages follow the ones above, and the section no longer folds.</summary>
    public List<string> HiddenHeadings { get; set; } = [];
}
