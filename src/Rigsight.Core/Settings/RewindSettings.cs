namespace Rigsight.Core.Settings;

/// <summary>
/// Rewind: a look back at a month, three months, six months or a year, as cards to watch in the app and share as
/// pictures. What is made, what goes in, what is never shown, and how a finished one is announced.
/// </summary>
public sealed class RewindSettings
{
    public bool Month { get; set; } = true;
    public bool Quarter { get; set; } = true;
    public bool Half { get; set; } = true;
    public bool Year { get; set; } = true;

    // What goes in (the user's defaults, 2026-10-19): time, games and internet on; heat, changes and problems off.
    public bool Time { get; set; } = true;
    public bool Games { get; set; } = true;
    public bool Internet { get; set; } = true;
    public bool Heat { get; set; }
    public bool Changes { get; set; }
    public bool Problems { get; set; }

    /// <summary>Games and apps (exe names) never shown in a rewind; their time still counts in the totals.</summary>
    public List<string> LeftOut { get; set; } = [];

    /// <summary>A finished rewind shows full screen the next time RigSight opens.</summary>
    public bool FullScreen { get; set; } = true;

    /// <summary>A notification when one is finished while RigSight is closed (the agent's, in the Notifications style).</summary>
    public bool Notify { get; set; } = true;

    /// <summary>The pictures' look in Share: a theme's and an accent's key, empty for the app's own.</summary>
    public string ShareTheme { get; set; } = "";
    public string ShareAccent { get; set; } = "";

    /// <summary>The pictures' shape in Share: "story", "post" or "wide".</summary>
    public string ShareShape { get; set; } = "story";

    public bool IsLeftOut(string? exe) => !string.IsNullOrEmpty(exe) && LeftOut.Contains(exe, StringComparer.OrdinalIgnoreCase);

    public bool Makes(Rewind.RewindKind kind) => kind switch
    {
        Rewind.RewindKind.Month => Month,
        Rewind.RewindKind.Quarter => Quarter,
        Rewind.RewindKind.Half => Half,
        _ => Year,
    };
}
