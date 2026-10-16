using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>A crash ready to display: what happened, why, what to try, and what was going on just before.</summary>
public sealed class CrashRow
{
    public required CrashEvent Event { get; init; }
    public required CrashExplanation Explanation { get; init; }
    public string? AppName { get; init; }
    public double? CpuBefore { get; init; }
    public double? GpuBefore { get; init; }

    /// <summary>Nothing was recorded in the five minutes before it: said, so no temperatures doesn't read as a cool PC.</summary>
    public bool NoReadingsBefore { get; init; }

    /// <summary>App in front just before (when Rigsight was recording).</summary>
    public string? FrontApp { get; init; }

    /// <summary>How long the crashed app had been in use in its session, in seconds (when Rigsight was recording).</summary>
    public double? SessionSec { get; init; }
    public bool IsGame { get; init; }

    /// <summary>The blue-screen dump file Windows saved, if any.</summary>
    public string? DumpPath { get; init; }

    /// <summary>What most likely happened, with how sure that is (see <see cref="CrashFinding"/>), and what it was worked out from.</summary>
    public CrashFinding? Finding { get; init; }
    public CrashInput? Input { get; init; }

    public bool HasFinding => Finding is not null;
    public string? Chip => Finding?.Chip;
    /// <summary>The colour that goes with how sure the finding is: "Named", "Likely", "Possible" or "NotEnough".</summary>
    public string Sure => (Finding?.Confidence ?? Confidence.NotEnough).ToString();
    /// <summary>The row's second line: what happened, in a sentence.</summary>
    public string Line => Finding?.Line ?? Reason;
    public Models.CrashSeverity Severity => Models.CrashGroup.SeverityOf(this);

    public DateTime Time => Event.Time;
    public string TimeText => Time.Date == DateTime.Today ? $"Today, {Time:h:mm tt}"
        : Time.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {Time:h:mm tt}"
        : Time.ToString("ddd d MMM, h:mm tt");
    public string Title => Explanation.Title;
    public string Reason => Explanation.Reason;
    public string Advice => Explanation.Advice;
    public string Culprit => Explanation.Culprit;
    public string? IconPath => Event.AppPath;

    public string KindLabel => Core.Wording.KindChip(Event.Kind);

    public bool IsSystem => Event.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset;

    /// <summary>"Just before: CPU 72°, GPU 84°" — only when Rigsight was recording at the time.</summary>
    public string? TempsText => CpuBefore is null && GpuBefore is null ? null
        : $"Just before: CPU {Core.Units.TempShort(CpuBefore)}, GPU {Core.Units.TempShort(GpuBefore)}" +
          (GpuBefore >= 83 || CpuBefore >= 88 ? " — running hot, which may have played a part." : "");

    /// <summary>"1h 12m into playing Crimson Desert · CPU 60°, GPU 78°" — what was going on, in one line.</summary>
    public string? ContextText
    {
        get
        {
            var parts = new List<string>();
            if (SessionSec is double s && s >= 60)
                parts.Add($"{Core.Units.Duration(s)} into {(IsGame ? "playing" : "using")} {AppName ?? "it"}");
            else if (FrontApp is not null && !string.Equals(FrontApp, AppName, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{FrontApp} was in front");
            if (CpuBefore is not null || GpuBefore is not null)
                parts.Add($"CPU {Core.Units.TempShort(CpuBefore)}, GPU {Core.Units.TempShort(GpuBefore)}" +
                          (GpuBefore >= 83 || CpuBefore >= 88 ? " (running hot)" : ""));
            else if (NoReadingsBefore)
                parts.Add(Core.Wording.NotRecorded.ToLowerInvariant());
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// Where the time shown isn't Windows' own (a PC going down is put at the last minute recorded before it, see
    /// <see cref="Core.Data.RigsightDb.LastSeenRunning"/>): what Windows' log says, so the two can be told apart by
    /// anyone who looks there. And where it is Windows' own (nothing was recorded then to check it by), that it can be
    /// early: the two kinds sit in one list, and only this tells them apart.
    /// </summary>
    public string? WindowsTimeNote => Event.Kind is not (CrashKind.SystemCrash or CrashKind.UnexpectedShutdown) ? null
        : Event.WindowsTs is not long w || Math.Abs(w - Event.Ts) < 60 ? "time from Windows' log, which is often earlier than the moment itself"
        : $"Windows' log says {Core.Data.TimeUtil.FromUnix(w).ToString(Core.Data.TimeUtil.FromUnix(w).Date == Time.Date ? "h:mm tt" : "ddd d MMM, h:mm tt")}, readings were recorded until {Time:h:mm tt}";

    public string TechnicalText => string.Join("  ·  ", new[]
    {
        string.IsNullOrEmpty(Event.AppExe) ? null : Event.Facts?.AppVersion is { } version ? $"{Event.AppExe} {version}" : Event.AppExe,
        Event.Module is null ? null : $"module {Event.Module}{(Event.Facts?.ModuleVersion is { } mv ? $" {mv}" : "")}{(Event.Facts?.Offset is { } offset ? $" at {offset}" : "")}",
        Event.Facts?.Driver is { } driver ? $"driver {driver}" : null,
        Event.Facts?.ReportType is { } type ? $"report {type}" : null,
        Event.Code is null ? null : $"code {Event.Code}",
        Event.Detail,
        WindowsTimeNote,
        DumpPath is null ? null : $"dump {DumpPath}",
    }.Where(s => !string.IsNullOrEmpty(s)));
}
