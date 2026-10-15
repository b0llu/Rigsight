using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Sampler thread: turns the ring of readings (<see cref="CloseUpRing"/>) into close-ups of app and game crashes and of
/// graphics driver resets (stored under no program: <see cref="CrashEvent.AppExe"/> is empty for them). The 5
/// minutes before are kept at once (the PC may go down next), the 2 after added once they are in. Nothing to turn on
/// and nothing left running: only a crash makes anything be written.
/// </summary>
internal sealed class CloseUpKeeper(RigsightDb db, CloseUpRing ring, Func<long>? nowMs = null)
{
    private readonly Func<long> _now = nowMs ?? (() => DateTimeOffset.Now.ToUnixTimeMilliseconds());

    /// <summary>An app crash with its before kept, waiting for its after.</summary>
    private readonly List<CrashEvent> _waiting = [];

    /// <summary>The least readings a close-up needs to be worth keeping (half a minute).</summary>
    internal const int LeastPoints = 15;

    /// <summary>The kinds that get a close-up: the PC was still running after them, so the readings around them are there.</summary>
    public static bool Kept(CrashKind kind) => kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset;

    /// <summary>Crashes just stored: one the ring still reaches gets its before kept now.</summary>
    public void OnStored(IReadOnlyList<CrashEvent> added)
    {
        long now = _now();
        foreach (var e in added.Where(e => Kept(e.Kind)))
        {
            // Noticed too late for the ring to hold much of its before (read from the log long after): nothing kept.
            if (ring.Oldest is not long oldest || e.Ts * 1000 < oldest + 60_000 || e.Ts * 1000 > now + 5_000) continue;
            if (_waiting.Any(w => w.Ts == e.Ts && w.AppExe.Equals(e.AppExe, StringComparison.OrdinalIgnoreCase))) continue;
            var before = ring.Between((e.Ts - CloseUp.BeforeSec) * 1000, (e.Ts + CloseUp.AfterSec) * 1000);
            if (before.Count < LeastPoints) continue;
            bool done = now >= (e.Ts + CloseUp.AfterSec) * 1000;
            db.SaveCloseUp(new CloseUp(e.Ts, e.AppExe, CloseUpKind.App, done, before));
            if (!done) _waiting.Add(e);
            Log.Write("closeup", $"Kept {before.Count} readings around {(e.Kind == CrashKind.GpuDriverReset ? "a graphics driver reset" : e.AppExe + "'s crash")} at {e.Time:HH:mm:ss}{(done ? "" : ", the after to come")}");
        }
    }

    /// <summary>Every sensor read: an after that is in is added.</summary>
    public void Tick()
    {
        long now = _now();
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            var e = _waiting[i];
            long end = (e.Ts + CloseUp.AfterSec) * 1000;
            // Its after is in (when the PC slept or tracking was paused meanwhile, what there is stands).
            if (now < end) continue;
            var points = ring.Between((e.Ts - CloseUp.BeforeSec) * 1000, end);
            var kept = db.GetCloseUp(e.Ts, e.AppExe);
            if (kept is null || points.Count >= kept.Points.Count) db.SaveCloseUp(new CloseUp(e.Ts, e.AppExe, CloseUpKind.App, true, points.Count >= LeastPoints ? points : kept?.Points ?? points));
            _waiting.RemoveAt(i);
        }
    }
}
