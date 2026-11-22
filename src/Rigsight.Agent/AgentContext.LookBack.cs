using Rigsight.Agent.Ui;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Drivers;
using Rigsight.Core.Stability;

namespace Rigsight.Agent;

// The tray's "Something just went wrong" (issue #15, approved mock-up v6, 2026-11-22): the person felt a stutter or a
// hitch, and the 2 minutes before are looked over (Core's LookBack decides), kept as a mark on the Timeline, and said in
// one notification whatever was found, nothing included. Read on the sampler thread, where the close-up ring is filled
// and the driver trace is started and stopped.
internal sealed partial class AgentContext
{
    /// <summary>
    /// The tray item (UI thread): the look back is queued for the sampler thread. Its notification is RigSight's own group
    /// (no daily limit, no Windows quiet time) and urgent (not held back behind a fullscreen app): it was just asked for.
    /// </summary>
    private void LookBackNow() => RunOnSampler(() =>
    {
        var now = DateTime.Now;
        long nowMs = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        try
        {
            // The trace's own ring, while the trace runs (admin rights only); null otherwise, and the text says so.
            var trace = _driverTrace is { Ended: false } running ? running : null;
            IReadOnlyList<DriverBurst>? bursts = trace?.Recent(nowMs - DriverRules.LookBackSec * 1000.0);
            var parts = bursts is { Count: > 0 } ? _db.GetDriverParts() : new Dictionary<string, DriverPart>(StringComparer.OrdinalIgnoreCase);
            long nowUnix = TimeUtil.ToUnix(now);
            var usual = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            int? UsualOf(string driver) => usual.TryGetValue(driver, out var u) ? u : usual[driver] = _db.GetDriverUsualLongest(driver, nowUnix);

            // The close-up ring is filled on this thread (Tracker, each sensor reading), so it is read here, not from the tray's.
            var points = _tracker.Ring.Between(nowMs - CloseUp.RingSec * 1000L, nowMs);
            var settings = _settings;
            Dictionary<long, AppRow>? apps = null;
            string? NameOf(long id)
            {
                apps ??= _db.LoadApps().ToDictionary(a => a.Id);
                return apps.TryGetValue(id, out var a) ? settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name : null;
            }

            // The sound trace's heard glitches (sound dropouts), while it runs; null otherwise, and the sound isn't checked.
            var sound = _soundTrace is { Ended: false } soundTrace ? soundTrace.Recent(nowMs - DriverRules.LookBackSec * 1000L) : null;

            var found = LookBack.Check(now, bursts, parts, UsualOf, points, NameOf, sound);
            try { _db.AddProblemMark(nowUnix, found.TimelineText); }
            catch (Exception ex) { _errors.Failed("look back mark", ex); }
            Log.Write("lookback", $"Look back at {now:HH:mm:ss}: "
                + (trace is null ? (_isAdmin ? "the driver trace isn't running, " : "no admin rights, ") : "")
                + $"{points.Count} readings kept from {(points.Count > 0 ? $"{TimeUtil.FromUnix(points[0].T / 1000):HH:mm:ss}" : "-")}; "
                + $"{(found.Found ? "found" : "nothing found")}: {found.Detail}; said \"{found.Title}\"");
            Notify(new Notice(NoticeKind.LookBack, found.Title, found.Body, Page: "timeline", Urgent: true));
        }
        catch (Exception ex) { _errors.Failed("look back", ex); }
    });
}
