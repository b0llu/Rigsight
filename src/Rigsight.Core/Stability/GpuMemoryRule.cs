namespace Rigsight.Core.Stability;

/// <summary>
/// The graphics card's memory just before an app crashed, against that app's own usual on this PC. Many games fill it
/// on purpose, so nearly full says nothing by itself: only well above what the same app uses here, and nearly full,
/// is said, and only as possible (it fits a crash from running out of it, and fits a game that simply loaded more).
/// </summary>
public sealed record GpuMemoryAtCrash(double UsedMb, double TotalMb, double UsualMb, int UsualMinutes, bool UpClose)
{
    /// <summary>The least minutes in front with the app's memory recorded that make a usual.</summary>
    public const int LeastMinutes = 30;

    /// <summary>Well above: at least this much more than usual (MB), and this share more.</summary>
    public const double MoreMb = 1024, MoreShare = 0.15;

    /// <summary>Nearly full: this share of all the card has.</summary>
    public const double NearlyFull = 0.9;

    public bool WellAbove => UsualMinutes >= LeastMinutes && TotalMb > 0 && UsedMb >= UsualMb + Math.Max(MoreMb, UsualMb * MoreShare)
        && UsedMb >= TotalMb * NearlyFull;

    /// <summary>The point said for it, or null when it isn't well above.</summary>
    public Evidence? Point(string app) => !WellAbove ? null : new("warn",
        Lang.T($"GPU memory was possibly running out: {UsedMb / 1024:0.0} of {TotalMb / 1024:0} GB"),
        UpClose
            ? Lang.T($"In the last 30 seconds. {app} usually uses about {UsualMb / 1024:0.0} GB on this PC. Running out of it can crash a game; a game that simply loaded more looks the same.")
            : Lang.T($"In its last minute. {app} usually uses about {UsualMb / 1024:0.0} GB on this PC. Running out of it can crash a game; a game that simply loaded more looks the same."));
}
