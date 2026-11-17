using Rigsight.Agent;
using Rigsight.Agent.Tracking;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Core.Storage;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Agent;

/// <summary>The drives' daily average temperature counts one reading per 5 minutes, however often the drives are read.</summary>
public sealed class DriveTempPaceTests
{
    [Fact]
    public void Readings_every_10_seconds_count_once_per_5_minutes_and_a_new_day_starts_over()
    {
        var pace = new DriveTempPace();
        long day = 100, t = 1_000_000;
        Assert.True(pace.Counts("A", day, t));
        for (int i = 1; i < 29; i++) Assert.False(pace.Counts("A", day, t + i * 10_000));
        Assert.True(pace.Counts("A", day, t + 300_000));
        Assert.True(pace.Counts("B", day, t + 10_000)); // each drive on its own
        Assert.True(pace.Counts("A", day + 86400, t + 310_000)); // a new day's first reading
        // A closed-app read a little early (a pass late the time before) still counts.
        Assert.True(pace.Counts("A", day + 86400, t + 310_000 + 295_000));
    }

    [Fact]
    public void The_average_weighs_each_5_minutes_the_same_while_the_highest_takes_every_reading()
    {
        using var t = new TestDb();
        long day = TimeUtil.ToUnix(new DateTime(2026, 11, 13));
        var pace = new DriveTempPace();
        DriveHealthInfo Read(double temp) => new() { Name = "SSD", Key = "K1", Status = "Good", Temperature = temp };
        long ms = 0;
        // 5 minutes with the app open (a reading every 10 s at 50, one at 52), then the app closed: the next at 35.
        for (int i = 0; i < 29; i++, ms += 10_000) t.Db.UpsertDriveHealth(day, Read(i == 15 ? 52 : 50), pace.Counts("K1", day, ms));
        t.Db.UpsertDriveHealth(day, Read(35), pace.Counts("K1", day, 300_000));
        var row = Assert.Single(t.Db.GetDriveHealthDays(day));
        Assert.Equal(52, row.TempMax);
        Assert.Equal(42.5, row.TempAvg!.Value, 1); // (50 + 35) / 2, not (28 × 50 + 52 + 35) / 30
    }
}

/// <summary>A big prune gives its space back to the disk.</summary>
public sealed class PruneShrinkTests
{
    [Fact]
    public void Prune_says_how_many_minutes_went_and_shrink_makes_the_file_smaller()
    {
        using var t = new TestDb();
        long start = TimeUtil.ToUnix(new DateTime(2026, 1, 1));
        using (var conn = t.Raw())
        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO system_minute(ts, cpu_temp, gpu_temp, cpu_load, gpu_load, ram_used) VALUES($ts, 50, 60, 10, 20, 8000)";
            var ts = cmd.Parameters.Add("$ts", Microsoft.Data.Sqlite.SqliteType.Integer);
            for (int i = 0; i < 60_000; i++)
            {
                ts.Value = start + i * 60L;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        t.OpenWriter();
        t.Db.Shrink(); // the write-ahead file folded in: the size before is the file's own
        long before = new FileInfo(t.Path).Length;

        int gone = t.Db.Prune(start + 45_000 * 60L);
        Assert.Equal(45_000, gone);
        Assert.True(gone >= RigsightDb.ShrinkAfterMinutes);
        t.Db.Shrink();
        long after = new FileInfo(t.Path).Length;
        Assert.True(after < before / 2, $"{before} -> {after} bytes");
        Assert.Equal(15_000L, t.Scalar("SELECT count(*) FROM system_minute"));
    }
}

/// <summary>Settings written by a newer RigSight keep what this one doesn't know; the app's change is laid onto the agent's.</summary>
public sealed class SettingsUnknownFieldsTests
{
    [Fact]
    public void Fields_from_a_newer_version_survive_a_read_and_a_write_and_its_number_is_never_lowered()
    {
        var json = SettingsStore.Serialize(new RigsightSettings { Theme = "midnight" })
            .Replace("\"Theme\":", "\"FromTheFuture\": {\"a\": [1, 2]},\n  \"Theme\":")
            .Replace("\"Overlay\": {", "\"Overlay\": {\n    \"NewOverlayThing\": true,")
            .Replace("\"SettingsVersion\": 0", "\"SettingsVersion\": 99");
        Assert.Contains("NewOverlayThing", json);
        Assert.Contains("\"SettingsVersion\": 99", json);
        var s = SettingsStore.Deserialize(json);
        Assert.Equal(99, s.SettingsVersion);
        var again = SettingsStore.Serialize(SettingsStore.Deserialize(SettingsStore.Serialize(s)));
        Assert.Contains("\"FromTheFuture\"", again);
        Assert.Contains("\"NewOverlayThing\": true", again);
        Assert.Contains("\"SettingsVersion\": 99", again);
        Assert.Contains("FromTheFuture", SettingsStore.Serialize(s.Clone()));
    }

    [Fact]
    public void Settings_RigSight_took_away_still_go_when_the_file_is_written()
    {
        // The agent rewrites a file that still has Ask's old switch to forget it (ForgetRiggySetting): kept as unknown, it never would.
        var s = SettingsStore.Deserialize("""{ "SettingsVersion": 13, "ShowAsk": true, "Tracking": { "KeepDetailedDays": 30 }, "Widgets": [ { "Style": "Pill", "Sits": "OverWindows" } ] }""");
        var json = SettingsStore.Serialize(s);
        Assert.DoesNotContain("ShowAsk", json);
        Assert.DoesNotContain("KeepDetailedDays", json);
        Assert.DoesNotContain("Sits", json);
    }

    [Fact]
    public void The_apps_change_is_laid_onto_what_the_agent_changed_since()
    {
        var copy = new RigsightSettings();
        string baseJson = SettingsStore.Serialize(copy);
        // Meanwhile the agent switched the taskbar readings off (its tray menu) and the overlay over (its shortcut)...
        var agentNow = copy.Clone();
        agentNow.TrayShown = !copy.TrayShown;
        agentNow.Overlay.Enabled = !copy.Overlay.Enabled;
        // ...while the app's change, made on the copy before, was on its way.
        var fromApp = copy.Clone();
        fromApp.UseFahrenheit = !copy.UseFahrenheit;
        fromApp.Alerts.CpuLimit = 91;

        var merged = AgentContext.LayOnto(fromApp, agentNow, baseJson);
        Assert.Equal(!copy.UseFahrenheit, merged.UseFahrenheit);
        Assert.Equal(91, merged.Alerts.CpuLimit);
        Assert.Equal(!copy.TrayShown, merged.TrayShown);
        Assert.Equal(!copy.Overlay.Enabled, merged.Overlay.Enabled);
    }

    [Fact]
    public void Where_both_changed_the_same_setting_the_apps_change_wins()
    {
        var copy = new RigsightSettings { TrayShown = true };
        var agentNow = copy.Clone();
        agentNow.TrayShown = false;
        var fromApp = copy.Clone();
        fromApp.WidgetsShown = !copy.WidgetsShown;
        fromApp.TrayShown = true; // unchanged by the app: the agent's switch stays
        var merged = AgentContext.LayOnto(fromApp, agentNow, SettingsStore.Serialize(copy));
        Assert.False(merged.TrayShown);
        Assert.Equal(!copy.WidgetsShown, merged.WidgetsShown);

        fromApp.TrayShown = false;
        agentNow.TrayShown = true;
        agentNow.TrayStyle = TrayStyle.TwoRows;
        var both = AgentContext.LayOnto(fromApp, agentNow, SettingsStore.Serialize(copy));
        Assert.False(both.TrayShown); // changed by both: the app's
        Assert.Equal(TrayStyle.TwoRows, both.TrayStyle);
    }
}

/// <summary>RivaTuner's busy bit: waited for briefly, and a write it blocks is never taken as done.</summary>
public sealed unsafe class RtssBusyTests
{
    [Fact]
    public void A_free_bit_is_taken_and_one_held_by_another_tool_is_given_up_on()
    {
        int busy = 0;
        Assert.True(Rtss.TakeBusy(&busy));
        Assert.Equal(1, busy & 1);
        // Still held (by us, standing in for Afterburner): not taken, and the bit left as it was.
        Assert.False(Rtss.TakeBusy(&busy));
        Assert.Equal(1, busy & 1);
    }
}

/// <summary>The crash scan, out of the agent's main class: what's stored, which catch-ups run, and which crashes become a card.</summary>
public sealed class CrashScannerTests
{
    private static readonly DateTime Now = DateTime.Now.Date.AddHours(12);

    private sealed class Logs
    {
        public List<CrashEvent> Events { get; } = [];
        public int ResetReads { get; private set; }

        public CrashScanner.Source Source => new(
            (since, systemOnly) => [.. Events.Where(e => e.Time >= since && (!systemOnly || e.Kind != CrashKind.AppCrash)).Select(Copy)],
            _ => [],
            _ => { ResetReads++; return []; });

        private static CrashEvent Copy(CrashEvent e) => new() { Ts = e.Ts, Kind = e.Kind, AppExe = e.AppExe, Moment = e.Moment };
    }

    private static CrashEvent Event(CrashKind kind, DateTime at) => new() { Ts = TimeUtil.ToUnix(at), Kind = kind };

    private static (CrashScanner Scanner, List<Notice> Said) Make(TestDb t, Logs logs)
    {
        var said = new List<Notice>();
        var settings = new RigsightSettings();
        settings.Alerts.CrashNotifications = true;
        return (new CrashScanner(t.Db, logs.Source, () => Now, () => settings, new CrashNoticeGate(), _ => { }, said.Add), said);
    }

    private static void HistoryFrom(TestDb t, DateTime from) =>
        t.Exec("INSERT INTO system_minute(ts) VALUES($ts)", ("$ts", TimeUtil.ToUnix(from)));

    [Fact]
    public void A_new_installs_first_scan_stores_crashes_without_a_card()
    {
        using var t = new TestDb();
        var logs = new Logs();
        logs.Events.Add(Event(CrashKind.SystemCrash, Now.AddHours(-3)));
        var (scanner, said) = Make(t, logs);
        scanner.Scan();
        Assert.True(scanner.Scanned);
        Assert.Single(t.Db.GetCrashes(0, long.MaxValue));
        Assert.Empty(said);
    }

    [Fact]
    public void With_history_a_shut_off_in_the_last_day_becomes_one_card()
    {
        using var t = new TestDb();
        HistoryFrom(t, Now.AddDays(-3));
        var logs = new Logs();
        logs.Events.Add(Event(CrashKind.UnexpectedShutdown, Now.AddHours(-2)));
        var (scanner, said) = Make(t, logs);
        scanner.Scan();
        var card = Assert.Single(said);
        Assert.Equal(NoticeKind.Crash, card.Kind);
        Assert.Equal("crashes", card.Page);
    }

    [Fact]
    public void Graphics_driver_resets_a_minute_apart_are_said_once()
    {
        using var t = new TestDb();
        HistoryFrom(t, Now.AddDays(-3));
        var logs = new Logs();
        logs.Events.Add(Event(CrashKind.GpuDriverReset, Now.AddMinutes(-3)));
        logs.Events.Add(Event(CrashKind.GpuDriverReset, Now.AddMinutes(-2)));
        var (scanner, said) = Make(t, logs);
        scanner.Scan();
        Assert.Equal(2, t.Db.GetCrashes(0, long.MaxValue).Count);
        Assert.Single(said);
    }

    [Fact]
    public void The_resets_catch_up_runs_once_on_the_scan_after_the_first()
    {
        using var t = new TestDb();
        HistoryFrom(t, Now.AddDays(-30));
        // Scanned before (a day ago): the first scan stays short, and leaves the catch-ups for the next one.
        t.Db.SetMeta(CrashScanner.CrashScannedKey, TimeUtil.ToUnix(Now.AddDays(-1)).ToString());
        var logs = new Logs();
        var (scanner, _) = Make(t, logs);
        scanner.Scan();
        Assert.True(scanner.FillDue);
        Assert.Equal(0, logs.ResetReads);
        scanner.Scan();
        Assert.Equal(1, logs.ResetReads);
        Assert.Equal("1", t.Db.GetMeta(CrashScanner.ResetsReadKey));
        Assert.Equal("1", t.Db.GetMeta(CrashScanner.CrashFactsKey));
        scanner.Scan();
        Assert.Equal(1, logs.ResetReads);
    }

    [Fact]
    public void A_first_scan_reading_all_90_days_needs_no_resets_catch_up()
    {
        using var t = new TestDb();
        var logs = new Logs();
        var (scanner, _) = Make(t, logs);
        scanner.Scan();
        scanner.Scan();
        Assert.Equal(0, logs.ResetReads);
        Assert.Equal("1", t.Db.GetMeta(CrashScanner.ResetsReadKey));
    }
}
