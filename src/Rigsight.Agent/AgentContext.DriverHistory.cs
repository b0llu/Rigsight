using System.Diagnostics;
using Rigsight.Agent.Drivers;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Agent;

// Every device's driver and each version it has had (1.22.0, "Drivers inside Timeline", approved mock-up v8): a look at
// every device's driver once a day (and soon after Windows' log shows a driver install), kept in the driver history. The
// first look also takes what Windows' log of driver installs still holds, for the weeks before. Sampler thread.
internal sealed partial class AgentContext
{
    /// <summary>When to look next (Environment.TickCount64): a few minutes after starting, so a PC that just started has less to do.</summary>
    private long _driverLookAt = Environment.TickCount64 + 4 * 60_000;

    /// <summary>A driver install was just seen in Windows' log: look before the day is up.</summary>
    private bool _driverLookSoon;

    private static readonly TimeSpan DriverLookEvery = TimeSpan.FromHours(24);

    /// <summary>Asks for a look in two minutes (an install takes a little while to settle).</summary>
    private void DriverLookSoon()
    {
        _driverLookSoon = true;
        _driverLookAt = Math.Min(_driverLookAt, Environment.TickCount64 + 2 * 60_000);
    }

    /// <summary>Called from the sampler's pass when <see cref="_driverLookAt"/> is due.</summary>
    private void LookAtDrivers()
    {
        _driverLookAt = Environment.TickCount64 + 60 * 60_000; // checked again in an hour if this one isn't due yet
        var now = DateTime.Now;
        DateTime? last = long.TryParse(_db.GetMeta(RigsightDb.DriverLookLastKey), out long l) ? TimeUtil.FromUnix(l) : null;
        bool first = !long.TryParse(_db.GetMeta(RigsightDb.DriverLookFirstKey), out _);
        if (!first && !_driverLookSoon && last is { } before && now - before < DriverLookEvery && now >= before) return;
        _driverLookSoon = false;

        long started = Stopwatch.GetTimestamp();
        var seen = DriverLook.Read();
        if (seen.Count == 0) return; // said by Read; tried again in an hour
        int makers = seen.Count(d => !d.Inbox), windows = seen.Count - makers;

        if (first)
        {
            var log = DriverLook.ReadLog(new DateTime(2000, 1, 1), out string said);
            var rows = DriverRecord.Seed(log, seen, now, DriverLook.NameOf);
            _db.PutDriverRows(rows);
            _db.SetMeta(RigsightDb.DriverLookFirstKey, TimeUtil.ToUnix(now).ToString(System.Globalization.CultureInfo.InvariantCulture));
            int devices = rows.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Log.Write("driver history", $"First look at every device's driver: {seen.Count} devices with a driver ({makers} from device makers, {windows} that come with Windows); "
                + $"{said}; {rows.Count} versions on record for {devices} devices, {rows.Count(r => r.How == DriverStart.Logged)} of them from the log; "
                + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            CompactMemory();
        }
        else
        {
            // Since the look before, an hour early so an install that was going on then is in it.
            var recent = DriverLook.ReadLog((last ?? now.AddDays(-2)).AddHours(-1), out string said);
            var stored = _db.GetDriverRows();
            var (write, changed) = DriverRecord.Update(stored, seen, now, recent);
            _db.PutDriverRows(write);
            var here = seen.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int missing = stored.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(k => !here.Contains(k));
            Log.Write("driver history", $"Looked at every device's driver: {seen.Count} devices with a driver ({makers} from device makers, {windows} that come with Windows), "
                + $"{changed.Count} changed{(changed.Count > 0 ? ": " + string.Join("; ", changed) : "")}; {missing} on record not here now; {said}; "
                + $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }
        _db.SetMeta(RigsightDb.DriverLookLastKey, TimeUtil.ToUnix(now).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _driverLookAt = Environment.TickCount64 + (long)DriverLookEvery.TotalMilliseconds;
    }
}
