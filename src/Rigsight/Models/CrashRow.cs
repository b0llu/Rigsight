using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>A crash ready to display: what happened, why, what to try, and how hot things were just before.</summary>
public sealed class CrashRow
{
    public required CrashEvent Event { get; init; }
    public required CrashExplanation Explanation { get; init; }
    public string? AppName { get; init; }
    public double? CpuBefore { get; init; }
    public double? GpuBefore { get; init; }

    public DateTime Time => Event.Time;
    public string TimeText => Time.Date == DateTime.Today ? $"Today, {Time:h:mm tt}"
        : Time.Date == DateTime.Today.AddDays(-1) ? $"Yesterday, {Time:h:mm tt}"
        : Time.ToString("ddd d MMM, h:mm tt");
    public string Title => Explanation.Title;
    public string Reason => Explanation.Reason;
    public string Advice => Explanation.Advice;
    public string Culprit => Explanation.Culprit;
    public string? IconPath => Event.AppPath;

    public string KindLabel => Event.Kind switch
    {
        CrashKind.AppCrash => "APP CRASH",
        CrashKind.AppHang => "NOT RESPONDING",
        CrashKind.GpuDriverReset => "GPU DRIVER RESET",
        CrashKind.SystemCrash => "BLUE SCREEN",
        _ => "UNEXPECTED SHUTDOWN",
    };

    public bool IsSystem => Event.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset;

    /// <summary>"Just before: CPU 72°, GPU 84°" — only when Rigsight was recording at the time.</summary>
    public string? TempsText => CpuBefore is null && GpuBefore is null ? null
        : $"Just before: CPU {Core.Units.TempShort(CpuBefore)}, GPU {Core.Units.TempShort(GpuBefore)}" +
          (GpuBefore >= 83 || CpuBefore >= 88 ? " — running hot, which may have played a part." : "");

    public string TechnicalText => string.Join("  ·  ", new[]
    {
        string.IsNullOrEmpty(Event.AppExe) ? null : Event.AppExe,
        Event.Module is null ? null : $"module {Event.Module}",
        Event.Code is null ? null : $"code {Event.Code}",
        Event.Detail,
    }.Where(s => !string.IsNullOrEmpty(s)));
}
