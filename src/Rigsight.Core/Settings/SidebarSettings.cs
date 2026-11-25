namespace Rigsight.Core.Settings;

/// <summary>How the app's sidebar is arranged (all by page key, e.g. "reports", "fans"; a dashboard's is "custom:" and its ID).</summary>
public sealed class SidebarSettings
{
    /// <summary>Fields this version doesn't know (written by a newer RigSight): kept and written back as they were, never dropped.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

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

    /// <summary>
    /// Which pages the sidebar was set to show at once (2026-10-25): "basic" (the few most people use), "full" (every
    /// page), or null when never picked. Picking one rewrites <see cref="Hidden"/>; the editor can change it after.
    /// </summary>
    public string? Pages { get; set; }

    /// <summary>A new install asks once which pages to show (set where no settings file existed yet).</summary>
    public bool AskPages { get; set; }

    /// <summary>The sidebar shows its pages' icons only, narrow; each page's name is its hover.</summary>
    public bool IconsOnly { get; set; }

    /// <summary>
    /// The pages this person has met (approved mock-up 2026-11-24): opened, or already there when All pages came. A built-in
    /// page not listed is new: it shows under its heading with a NEW tag until opened. Null until this version first reads
    /// the sidebar, which then lists every page the person's last version had (a new install: every page).
    /// </summary>
    public List<string>? Known { get; set; }

    /// <summary>Pages whose sidebar switch the person set themselves: a basics sidebar doesn't send them back to All pages after a first visit.</summary>
    public List<string> Chosen { get; set; } = [];

    /// <summary>New pages whose Home card was put off with Later: the card stays away, the NEW tag stays until the page is opened.</summary>
    public List<string> CardsLater { get; set; } = [];

    /// <summary>The one Home card that tells someone who updated where All pages is: waiting until closed or All pages is opened.</summary>
    public bool AllPagesCard { get; set; }
}
