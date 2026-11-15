using Rigsight.Core;

namespace Rigsight.Agent;

/// <summary>
/// An error in one part of something that runs every second: logged in full the first time, then (the same part, the same
/// kind of error) at most once every 10 minutes with how many times it came in between. A failure that repeats every
/// second would otherwise rotate the rest of the log away within the hour, and with it what came before.
/// </summary>
internal sealed class RepeatedErrors(string area, Func<long>? clock = null, Action<string>? write = null)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Action<string> _write = write ?? (line => Log.Write(area, line));
    private readonly Dictionary<string, (long At, int Since, string Kind)> _last = new(StringComparer.Ordinal);

    /// <summary><paramref name="part"/> failed with <paramref name="ex"/>; the caller goes on with the rest.</summary>
    public void Failed(string part, Exception ex)
    {
        long now = _clock();
        string kind = ex.GetType().FullName ?? ex.GetType().Name;
        string? line = null;
        lock (_last)
        {
            bool known = _last.TryGetValue(part, out var last) && last.Kind == kind;
            if (known && now - last.At < (long)Every.TotalMilliseconds)
            {
                _last[part] = last with { Since = last.Since + 1 };
                return;
            }
            _last[part] = (now, 0, kind);
            line = known && last.Since > 0
                ? $"{part} failed again, {last.Since} more times in the last {Every.TotalMinutes:0} minutes: {ex}"
                : $"{part} failed: {ex}";
        }
        _write(line);
    }

    /// <summary>Runs <paramref name="step"/>; a failure is noted under <paramref name="part"/> and the caller goes on.</summary>
    public void Run(string part, Action step)
    {
        try { step(); }
        catch (Exception ex) { Failed(part, ex); }
    }
}
