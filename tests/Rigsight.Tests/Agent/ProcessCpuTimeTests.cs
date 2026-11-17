using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Agent.Tracking;
using Rigsight.Core.Data;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Each app's CPU time between two samples, from the same one call that lists the processes (tried on processes of the
/// test's own making, so the figures are known), and what the tracker keeps of it a minute at a time.
/// </summary>
public sealed class ProcessCpuTimeTests
{
    private sealed record Proc(int Pid, string Exe, long Created, long Kernel, long User);

    private static readonly int Size = Marshal.SizeOf<Win32.SYSTEM_PROCESS_INFORMATION>();
    private const long Second = TimeSpan.TicksPerSecond;
    // The samples' wall-clock time, as Windows dates a process's start.
    private static readonly long T0 = new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    private static ProcessSnapshot Read(ProcessSampler sampler, double atSeconds, params Proc[] procs)
    {
        IntPtr buffer = Marshal.AllocHGlobal(procs.Length * Size);
        var names = new List<IntPtr>();
        try
        {
            for (int i = 0; i < procs.Length; i++)
            {
                var p = procs[i];
                names.Add(Marshal.StringToHGlobalUni(p.Exe));
                Marshal.StructureToPtr(new Win32.SYSTEM_PROCESS_INFORMATION
                {
                    NextEntryOffset = i == procs.Length - 1 ? 0 : (uint)Size,
                    UniqueProcessId = p.Pid, CreateTime = p.Created, KernelTime = p.Kernel, UserTime = p.User,
                    ImageName = new Win32.UNICODE_STRING { Length = (ushort)(p.Exe.Length * 2), MaximumLength = (ushort)(p.Exe.Length * 2 + 2), Buffer = names[^1] },
                }, buffer + i * Size, false);
            }
            return sampler.Read(buffer, (long)(atSeconds * Stopwatch.Frequency), T0 + (long)(atSeconds * Second));
        }
        finally
        {
            foreach (var name in names) Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void An_apps_cpu_time_is_what_its_processes_used_between_two_samples_every_core_counted()
    {
        var sampler = new ProcessSampler();
        var first = Read(sampler, 10,
            new Proc(4, "System", 0, 500 * Second, 0),
            new Proc(100, "chrome.exe", T0 - 3600 * Second, 10 * Second, 20 * Second),
            new Proc(101, "chrome.exe", T0 - 3000 * Second, 1 * Second, 1 * Second),
            new Proc(200, "game.exe", T0 - 600 * Second, 0, 300 * Second));
        // Nothing to set the first sample against: no time yet, even for what has run for an hour.
        Assert.Equal(0, first.CpuTime);
        Assert.All(first.Apps.Values, a => Assert.Equal(0, a.CpuTime));

        // Five seconds on: the game kept three cores busy (more than the 100% a share is held to on a small PC), the two
        // of Chrome a second between them, Windows' own half a second.
        var second = Read(sampler, 15,
            new Proc(4, "System", 0, 500 * Second + Second / 2, 0),
            new Proc(100, "chrome.exe", T0 - 3600 * Second, 10 * Second + Second / 4, 20 * Second + Second / 4),
            new Proc(101, "chrome.exe", T0 - 3000 * Second, 1 * Second + Second / 2, 1 * Second),
            new Proc(200, "game.exe", T0 - 600 * Second, 0, 315 * Second));
        Assert.Equal(15 * Second, second.Apps["game.exe"].CpuTime);
        Assert.Equal(Second, second.Apps["chrome.exe"].CpuTime);
        Assert.Equal(16 * Second + Second / 2, second.CpuTime); // every process, Windows' own among them
        Assert.False(second.Apps.ContainsKey("System"));
    }

    [Fact]
    public void A_process_started_since_the_last_sample_counts_in_full_and_a_reused_id_doesnt()
    {
        var sampler = new ProcessSampler();
        Read(sampler, 10, new Proc(300, "old.exe", T0 - 60 * Second, 0, 40 * Second));
        var second = Read(sampler, 12,
            // ID 300 again, for a process started after the last sample: a new one, all its time since.
            new Proc(300, "build.exe", T0 + 11 * Second, 0, Second / 2),
            // Started before the last sample and missed by it (it can't be): nothing to set it against.
            new Proc(400, "early.exe", T0 + 5 * Second, 0, 3 * Second),
            new Proc(500, "quick.exe", T0 + 10 * Second + 1, Second / 10, Second / 10));
        Assert.Equal(Second / 2, second.Apps["build.exe"].CpuTime);
        Assert.Equal(0, second.Apps["early.exe"].CpuTime);
        Assert.Equal(Second / 5, second.Apps["quick.exe"].CpuTime);
        Assert.Equal(Second / 2 + Second / 5, second.CpuTime);
    }

    [Fact]
    public void The_tracker_keeps_each_apps_times_by_the_hour_and_the_day()
    {
        using var rig = new TrackerRig(start: FakeClock.At(10));
        rig.Settings.Tracking.ExcludedApps.Add("secret.exe");
        rig.Running.UnionWith(["discord.exe", "secret.exe", "System", "helper.exe"]);
        rig.Usage["discord.exe"] = (1, 400);
        rig.Usage["helper.exe"] = (0.1, 20);
        rig.CpuTime["game.exe"] = 2 * Second;            // each 5-second sample: 2 seconds of CPU time
        rig.GpuTime["game.exe"] = 4 * Second;
        rig.CpuTime["discord.exe"] = Second / 2;
        rig.CpuTime["secret.exe"] = 9 * Second;          // left out of tracking: nothing kept
        rig.CpuTime["System"] = 3 * Second;              // no app, but in the total
        rig.CpuTime["helper.exe"] = Second / 1000;       // a millisecond a sample: too little to be added to the apps
        rig.SystemCpuTime = Second;
        rig.Use("game.exe", 120);                        // two minutes in front: 24 samples
        rig.Tracker.Flush(closeAllSessions: false);

        long game = rig.AppId("game.exe"), discord = rig.AppId("discord.exe");
        var hours = rig.Db.GetAppCpuHours(game, 0, long.MaxValue / 2);
        var day = rig.Db.GetAppCpuDays(game, 0, long.MaxValue / 2).Single();
        Assert.Equal(24 * 2000, hours.Sum(h => h.CpuMs));
        Assert.Equal(24 * 4000, hours.Sum(h => h.GpuMs));
        Assert.Equal(24 * 2000, hours.Sum(h => h.FgCpuMs)); // all of it in front
        Assert.Equal(120, hours.Sum(h => h.FgSec));
        Assert.Equal((24 * 2000L, 120L), (day.CpuMs, day.RunSec));
        Assert.Equal(300, day.MemMbAvg!.Value, 6); // the rig's default memory

        var bg = rig.Db.GetAppCpuDays(discord, 0, long.MaxValue / 2).Single();
        Assert.Equal((24 * 500L, 0L, 0L, 120L), (bg.CpuMs, bg.FgCpuMs, bg.FgSec, bg.RunSec));
        Assert.Equal(400, bg.MemMbAvg!.Value, 6);

        Assert.False(rig.HasApp("secret.exe"));
        Assert.False(rig.HasApp("System"));
        Assert.False(rig.HasApp("helper.exe"));
        // Every process together: the apps, the left-out one and Windows' own (the hour's total, and the day's).
        long all = 24 * (2000 + 500 + 9000 + 3000 + 1 + 1000);
        Assert.Equal(all, rig.Db.GetAppCpuHours(AppCpuUse.AllApps, 0, long.MaxValue / 2).Sum(h => h.CpuMs));
        Assert.Equal(all, rig.Db.GetAppCpuDays(AppCpuUse.AllApps, 0, long.MaxValue / 2).Single().CpuMs);

        // Saved early (Windows signing out) and then the minute's end: nothing counted twice.
        rig.Use("game.exe", 30);
        rig.Tracker.Flush(closeAllSessions: true);
        rig.Use("game.exe", 30);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Equal(36 * 2000, rig.Db.GetAppCpuDays(game, 0, long.MaxValue / 2).Single().CpuMs);
    }

    [Fact]
    public void Nothing_is_kept_while_tracking_is_paused()
    {
        using var rig = new TrackerRig();
        rig.Settings.Tracking.PausedUntil = rig.Clock.Unix + 600;
        rig.CpuTime["game.exe"] = 2 * Second;
        rig.SystemCpuTime = Second;
        rig.Use("game.exe", 120);
        rig.Tracker.Flush(closeAllSessions: false);
        Assert.Empty(rig.Db.GetAppCpuDays(AppCpuUse.AllApps, 0, long.MaxValue / 2));
        Assert.False(rig.HasApp("game.exe"));
    }
}
