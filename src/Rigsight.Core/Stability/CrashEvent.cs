namespace Rigsight.Core.Stability;

public enum CrashKind
{
    /// <summary>An app or game crashed (Windows "Application Error").</summary>
    AppCrash,
    /// <summary>An app stopped responding and was closed.</summary>
    AppHang,
    /// <summary>The graphics driver stopped responding and Windows reset it (TDR).</summary>
    GpuDriverReset,
    /// <summary>Windows itself crashed (blue screen / bugcheck).</summary>
    SystemCrash,
    /// <summary>The PC turned off without shutting down properly, with no crash report.</summary>
    UnexpectedShutdown,
}

/// <summary>What Windows was doing when the PC went down. Stored as is in crashes.during_sleep (0, 1, 2).</summary>
public enum PowerMoment
{
    Running,
    /// <summary>Asleep or waking up.</summary>
    Asleep,
    /// <summary>Shutting down, restarting or hibernating: the PC went off before Windows recorded a clean finish.</summary>
    ShuttingDown,
}

/// <summary>One crash or unexpected shutdown, as found in the Windows event logs.</summary>
public sealed class CrashEvent
{
    public long Id { get; set; }
    /// <summary>When it happened (Unix seconds).</summary>
    public long Ts { get; set; }
    public CrashKind Kind { get; set; }
    public string AppExe { get; set; } = "";
    public string? AppPath { get; set; }
    /// <summary>The DLL or driver where the crash happened, if known.</summary>
    public string? Module { get; set; }
    /// <summary>Exception or bugcheck code, e.g. "c0000005" or "0x133".</summary>
    public string? Code { get; set; }
    public string? Detail { get; set; }
    /// <summary>What Windows was doing when the PC went down (unexpected shutdowns only).</summary>
    public PowerMoment Moment { get; set; }

    /// <summary>The PC was asleep or waking up when it went down.</summary>
    public bool DuringSleep
    {
        get => Moment == PowerMoment.Asleep;
        set => Moment = value ? PowerMoment.Asleep : PowerMoment.Running;
    }

    public DateTime Time => Data.TimeUtil.FromUnix(Ts);
}
