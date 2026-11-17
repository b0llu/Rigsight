using Rigsight.Agent.Ui;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Agent.Tracking;

/// <summary>
/// Sampler thread: copies new crash records from Windows' event logs into the database, runs the one-time catch-ups
/// once each, and decides which crashes become a card. Out of the agent's main class so it can be tested against a
/// test database and made-up logs: this decides whether crashes are found and said, on every PC.
/// </summary>
internal sealed class CrashScanner
{
    /// <summary>Where crash records come from: Windows' event logs, or a test's own.</summary>
    internal sealed record Source(
        Func<DateTime, bool, List<CrashEvent>> ReadSince,
        Func<DateTime, List<CrashSign>> ReadSigns,
        Func<DateTime, List<CrashEvent>> ReadResets)
    {
        public static readonly Source WindowsLogs = new(
            (since, systemOnly) => CrashLogReader.ReadSince(since, systemOnly), CrashLogReader.ReadSigns, CrashLogReader.ReadResets);
    }

    internal const string ShutdownTimesKey = "shutdown_times_placed";
    internal const string CrashFactsKey = "crash_facts_filled";
    internal const string ResetsReadKey = "gpu_resets_read";
    internal const string CrashScannedKey = RigsightDb.CrashScannedKey;

    private readonly RigsightDb _db;
    private readonly Source _source;
    private readonly Func<DateTime> _now;
    private readonly Func<RigsightSettings> _settings;
    private readonly CrashNoticeGate _gate;
    private readonly Action<IReadOnlyList<CrashEvent>> _onStored;
    private readonly Action<Notice> _notify;

    /// <param name="onStored">The crashes just added (the close-ups take them).</param>
    /// <param name="notify">A card to show.</param>
    public CrashScanner(RigsightDb db, Source source, Func<DateTime> now, Func<RigsightSettings> settings, CrashNoticeGate gate,
        Action<IReadOnlyList<CrashEvent>> onStored, Action<Notice> notify)
    {
        (_db, _source, _now, _settings, _gate, _onStored, _notify) = (db, source, now, settings, gate, onStored, notify);
    }

    /// <summary>The first scan since the agent started is done.</summary>
    public bool Scanned { get; private set; }

    /// <summary>The one-time catch-up (see <see cref="CrashFactsKey"/>) is still to do: it is the scan after the first.</summary>
    public bool FillDue { get; private set; }

    /// <summary>
    /// How far back the first scan after starting reads: a day before the last scan ended (Windows files a crash's
    /// report, and a blue screen's driver, some minutes after the crash's own entry), and never more than 90 days. On a
    /// PC never scanned, the 90 days. It used to be 90 days at every start: about 100 ms of CPU on a PC a few weeks old.
    /// </summary>
    internal static DateTime FirstScanFrom(string? scannedTo, DateTime now)
    {
        var furthest = now.AddDays(-90);
        if (!long.TryParse(scannedTo, out long ts)) return furthest;
        var from = TimeUtil.FromUnix(ts).AddDays(-1);
        return from < furthest ? furthest : from > now ? now.AddDays(-1) : from;
    }

    public void Scan()
    {
        try
        {
            var now = _now();
            bool firstScan = !Scanned;
            var since = firstScan ? FirstScanFrom(_db.GetMeta(CrashScannedKey), now) : now.AddMinutes(-30);
            // Once: the PC going down, stored under Windows' time (often half an hour early) by versions before 0.19.1,
            // is put at the time it was last known to be running. The scan below does that for the last 90 days; this
            // is for the ones stored from before, as far back as Windows' log still has them.
            if (_db.GetMeta(ShutdownTimesKey) is null)
            {
                if (_db.FirstCrashTime() is long first && TimeUtil.FromUnix(first) < since)
                    _db.InsertCrashes(_source.ReadSince(TimeUtil.FromUnix(first).AddDays(-1), true).Where(e => e.NextStart is not null), knownOnly: true);
                _db.SetMeta(ShutdownTimesKey, "1");
            }
            // Once: what else Windows wrote about the crashes stored before that was kept (versions, the driver a blue
            // screen names, a held power button…), as far back as its logs still reach; and the trouble it logged
            // around them. The scan below does both for what it reads.
            // That one time the logs are read once for both, from the first stored crash; and not by the first scan
            // after starting, which stays short (see FirstScanFrom).
            var from = since;
            bool fillDue = _db.GetMeta(CrashFactsKey) is null;
            bool fill = fillDue && !firstScan;
            // A first scan that reads the whole 90 days finds the resets itself: nothing to catch up on.
            if (firstScan && since <= now.AddDays(-89) && _db.GetMeta(ResetsReadKey) is null) _db.SetMeta(ResetsReadKey, "1");
            FillDue = (fillDue || _db.GetMeta(ResetsReadKey) is null) && firstScan;
            if (fill && _db.FirstCrashTime() is long oldest && TimeUtil.FromUnix(oldest).AddDays(-1) is var earliest && earliest < since) from = earliest;
            _db.InsertSigns(_source.ReadSigns(from));
            var read = _source.ReadSince(from, false);
            long sinceTs = TimeUtil.ToUnix(since);
            // Older than the scan's own reach: only what is stored already gets its facts, nothing is added.
            if (from < since) _db.InsertCrashes(read.Where(e => e.Ts < sinceTs), knownOnly: true);
            var added = _db.InsertCrashes(read.Where(e => e.Ts >= sinceTs));
            // An app's crash the ring of readings still reaches gets its close-up.
            try { _onStored(added); }
            catch (Exception ex) { Log.Error("closeup", ex); }
            if (fill) _db.SetMeta(CrashFactsKey, "1");
            // Once, from 0.25.0 on: the graphics driver resets Windows recorded as hang reports, which earlier versions
            // didn't read, as far back as a first scan reads; and the moment each hang really happened, for a shut-off
            // that came with one. On the scan after the first, as the catch-up above, to keep starting light.
            if (!firstScan && _db.GetMeta(ResetsReadKey) is null)
            {
                var back = now.AddDays(-90);
                var resets = _db.InsertCrashes(_source.ReadResets(back));
                _db.InsertSigns(_source.ReadSigns(back));
                // A shut-off that came right after the card hung was running until then: placed again (later, never earlier).
                _db.InsertCrashes(_source.ReadSince(back, true).Where(e => e.NextStart is not null), knownOnly: true);
                _db.SetMeta(ResetsReadKey, "1");
                Log.Write("crash", $"Read the graphics driver resets of the last 90 days: {resets.Count} found");
            }
            _db.SetMeta(CrashScannedKey, TimeUtil.ToUnix(now).ToString());
            Scanned = true;
            // Rigsight's own crashes, and the PC's, as Windows logged them: a program that died wrote nothing here itself.
            foreach (var e in added.Where(e => e.Time > now.AddDays(-7)
                         && (e.AppExe.StartsWith("Rigsight", StringComparison.OrdinalIgnoreCase) || e.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset)).TakeLast(15))
                Log.Write("crash", $"Windows logged at {e.Time:yyyy-MM-dd HH:mm:ss}: {e.Kind}{(e.AppExe.Length > 0 ? " of " + e.AppExe : "")}"
                    + $"{(e.Module is null ? "" : ", in " + e.Module)}{(e.Code is null ? "" : ", code " + e.Code)}{(e.Kind == CrashKind.UnexpectedShutdown ? ", " + e.Moment : "")}");
            var settings = _settings();
            if (!settings.Alerts.CrashNotifications) return;
            // A new install's first look at Windows' logs: nothing as a card (the Crashes page has them, and the welcome
            // is up). Cards popped up beside the welcome for a crash the day before RigSight was installed (2026-10-30).
            if (firstScan && (_db.FirstDataTime() is not long recordedFrom || now - TimeUtil.FromUnix(recordedFrom) < TimeSpan.FromHours(1)))
            {
                Log.Write("crash", "First look on a new install: no crash cards, the Crashes page has them");
                return;
            }

            // Tell the user about system-level problems they may not have noticed (app crashes are covered by sessions).
            // A shutdown that didn't finish is usually harmless and on some PCs happens every day: the Crashes page has it.
            var recent = added.Where(e => e.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset
                                          && e.Moment != PowerMoment.ShuttingDown
                                          && e.Time > now.AddHours(firstScan ? -24 : -1)).ToList();
            if (recent.Count == 0) return;
            // As stored (a PC going down is given the time it was last seen running), with each one's finding.
            var stored = _db.GetCrashes(recent.Min(e => e.Ts) - 1, recent.Max(e => e.Ts) + 1)
                .Where(s => recent.Any(e => e.Kind == s.Kind && e.Ts == s.Ts)).OrderBy(s => s.Ts).ToList();
            var findings = CrashCases.Of(_db, stored, settings);
            foreach (var e in stored.TakeLast(2))
            {
                // A graphics card that hangs again and again within minutes: the first is said, the rest are on the page.
                if (e.Kind == CrashKind.GpuDriverReset && !_gate.Allow("graphics driver", e.Time)) continue;
                var ex = CrashExplainer.Explain(e);
                // The title says what happened ("Your PC shut off suddenly"); when and why under it, not the event again.
                string said = NoticeWords.PcDownBody(e.Time, now, findings.GetValueOrDefault(e.Id), ex.Reason);
                _notify(new Notice(NoticeKind.Crash, NoticeWords.PcDownTitle(e.Kind), said, Page: "crashes", Arg: e.Time.Ticks.ToString()));
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
        }
    }
}
