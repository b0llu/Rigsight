using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Models;

/// <summary>
/// The sidebar card about the history file: what's wrong or what was done, and the buttons for it. Only damage the check
/// confirmed and repairs that lost something get a card; a repair that lost nothing and rebuilt lookup lists are logged.
/// </summary>
public sealed record HistoryCard(string Kind, string Title, string Text)
{
    /// <summary>Warm (needs a look) or calm.</summary>
    public bool IsWarm => Kind is "damaged" or "waiting" or "nothing";
    public bool CanRepair => Kind == "damaged";
    public bool CanRetry => Kind == "waiting";
    public bool CanOpenFolder => Kind is "repaired" or "nothing";
    public bool CanDismiss => Kind is not "waiting";
    public string DismissText => Kind == "damaged" ? Lang.T("Not now") : Lang.T("Dismiss");

    public static HistoryCard? Of(HistoryFileState? h)
    {
        if (h is null || h.Dismissed && h.Outcome != "waiting") return null;
        if (h.Outcome == "waiting")
            return new("waiting", Lang.T("History repair is waiting"), h.WaitingReason ?? Lang.T("It tries again by itself every 10 minutes."));
        if (h.Damaged)
            return new("damaged", Lang.T("Part of your history can't be read"), Lang.T("Some older readings can't be read. New readings save as usual."));
        return h.Outcome switch
        {
            "repaired" => new("repaired", Lang.T("History repaired"), h.Summary ?? Lang.T("The damaged file is kept.")),
            "nothing" => new("nothing", Lang.T("Your history couldn't be saved"), h.Summary ?? Lang.T("RigSight started a new history today. The old file is kept.")),
            _ => null,
        };
    }
}
