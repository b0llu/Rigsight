namespace Rigsight.Core.Settings;

/// <summary>
/// How the Timeline page lists the days: its Group choice and the kinds of line that fold into one on a day (the Fold
/// list, the user 2026-11-26: "give them the option to group up the events that they want").
/// </summary>
public sealed class TimelineSettings
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>"Day", "Month" or "Each" (Off); null until picked, which is "Day".</summary>
    public string? Grouping { get; set; }

    /// <summary>
    /// The kinds ticked under Fold ("sound", "apps", "games", "problems", "delays", "marks"); null until someone changes
    /// the list, which is sound cut-outs alone (the user, 2026-11-26). A kind added later is never in a saved list, so it
    /// starts unticked and no update changes what someone ticked.
    /// </summary>
    public List<string>? Folds { get; set; }
}
