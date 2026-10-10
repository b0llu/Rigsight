using System.Diagnostics;
using System.Runtime.InteropServices;
using Rigsight.Agent.Native;
using Rigsight.Agent.Tracking;

namespace Rigsight.Tests.Agent;

/// <summary>
/// What each app reads and writes, worked out from the same one call that lists the processes: the bytes Windows has
/// counted for a process since it started, set against the sample before. Tried on processes of the test's own making
/// (laid out as Windows lays them out), so the figures are known.
/// </summary>
public sealed class ProcessDiskTests
{
    private sealed record Proc(int Pid, string Exe, long Created, long Read, long Write, long CpuTime = 0, long MemBytes = 0);

    private static readonly int Size = Marshal.SizeOf<Win32.SYSTEM_PROCESS_INFORMATION>();

    /// <summary>The processes as NtQuerySystemInformation hands them over: one entry after another, the last with no next.</summary>
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
                    UniqueProcessId = p.Pid, CreateTime = p.Created, UserTime = p.CpuTime, WorkingSetPrivateSize = p.MemBytes,
                    ReadTransferCount = p.Read, WriteTransferCount = p.Write,
                    // Counted too by Windows, and no part of what is read and written.
                    OtherTransferCount = 999_999_999, ReadOperationCount = 77, WriteOperationCount = 88, OtherOperationCount = 99,
                    ImageName = new Win32.UNICODE_STRING { Length = (ushort)(p.Exe.Length * 2), MaximumLength = (ushort)(p.Exe.Length * 2 + 2), Buffer = names[^1] },
                }, buffer + i * Size, false);
            }
            return sampler.Read(buffer, (long)(atSeconds * Stopwatch.Frequency));
        }
        finally
        {
            foreach (var name in names) Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const long MB = 1 << 20;

    [Fact]
    public void The_counters_sit_where_Windows_puts_them()
    {
        // The layout of SYSTEM_PROCESS_INFORMATION on 64-bit Windows: read and written bytes at 0xE8 and 0xF0, 0x100 in all.
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(0x100, Size);
        Assert.Equal(0x50, (int)Marshal.OffsetOf<Win32.SYSTEM_PROCESS_INFORMATION>(nameof(Win32.SYSTEM_PROCESS_INFORMATION.UniqueProcessId)));
        Assert.Equal(0x90, (int)Marshal.OffsetOf<Win32.SYSTEM_PROCESS_INFORMATION>(nameof(Win32.SYSTEM_PROCESS_INFORMATION.WorkingSetSize)));
        Assert.Equal(0xD0, (int)Marshal.OffsetOf<Win32.SYSTEM_PROCESS_INFORMATION>(nameof(Win32.SYSTEM_PROCESS_INFORMATION.ReadOperationCount)));
        Assert.Equal(0xE8, (int)Marshal.OffsetOf<Win32.SYSTEM_PROCESS_INFORMATION>(nameof(Win32.SYSTEM_PROCESS_INFORMATION.ReadTransferCount)));
        Assert.Equal(0xF0, (int)Marshal.OffsetOf<Win32.SYSTEM_PROCESS_INFORMATION>(nameof(Win32.SYSTEM_PROCESS_INFORMATION.WriteTransferCount)));
    }

    [Fact]
    public void An_apps_disk_use_is_what_its_processes_read_and_wrote_between_two_samples()
    {
        var sampler = new ProcessSampler { Detail = new HashSet<string>(["chrome.exe"], StringComparer.OrdinalIgnoreCase) };
        var first = Read(sampler, 10,
            new Proc(4, "System", 0, 500 * MB, 900 * MB),
            new Proc(100, "chrome.exe", 5000, 10 * MB, 20 * MB),
            new Proc(101, "chrome.exe", 3000, 1 * MB, 1 * MB),
            new Proc(200, "game.exe", 7000, 4000 * MB, 0),
            new Proc(300, "idle.exe", 8000, 50 * MB, 50 * MB));
        // Nothing to set the first sample against: no use yet, as with the processor.
        Assert.Equal(0, first.Disk);
        Assert.All(first.Apps.Values, a => Assert.Equal(0, a.Disk));

        // Two seconds on: the two of Chrome read 6 MB and wrote 2, the game read 100 MB, Windows itself wrote 10.
        var second = Read(sampler, 12,
            new Proc(4, "System", 0, 500 * MB, 910 * MB),
            new Proc(100, "chrome.exe", 5000, 14 * MB, 22 * MB),
            new Proc(101, "chrome.exe", 3000, 3 * MB, 1 * MB),
            new Proc(200, "game.exe", 7000, 4100 * MB, 0),
            new Proc(300, "idle.exe", 8000, 50 * MB, 50 * MB));
        Assert.Equal(4 * MB, second.Apps["chrome.exe"].Disk, 3);
        Assert.Equal(50 * MB, second.Apps["game.exe"].Disk, 3);
        Assert.Equal(0, second.Apps["idle.exe"].Disk);
        // Each process on its own, for the apps asked about.
        Assert.Equal([3.0 * MB, 1.0 * MB], second.Apps["chrome.exe"].Processes!.Select(p => Math.Round(p.Disk)));
        Assert.Null(second.Apps["game.exe"].Processes);
        // The total is every process together, with what Windows wrote in its own name: more than the apps listed add up to.
        Assert.False(second.Apps.ContainsKey("System"));
        Assert.Equal(59 * MB, second.Disk, 3);
        // An app started when its earliest process did.
        Assert.Equal(3000, second.Apps["chrome.exe"].Created);
        Assert.Equal(7000, second.Apps["game.exe"].Created);
    }

    [Fact]
    public void A_new_process_or_a_reused_ID_starts_from_nothing()
    {
        var sampler = new ProcessSampler();
        Read(sampler, 10, new Proc(100, "old.exe", 5000, 900 * MB, 900 * MB));
        // The ID given to another process (it started later), and one that was not there before: neither has a sample
        // before to be set against, however much each has already read.
        var next = Read(sampler, 12, new Proc(100, "new.exe", 6000, 10 * MB, 0), new Proc(101, "late.exe", 6500, 300 * MB, 0));
        Assert.Equal(0, next.Apps["new.exe"].Disk);
        Assert.Equal(0, next.Apps["late.exe"].Disk);
        Assert.Equal(0, next.Disk);
        // From the next sample on they count.
        var after = Read(sampler, 13, new Proc(100, "new.exe", 6000, 12 * MB, 0), new Proc(101, "late.exe", 6500, 300 * MB, MB));
        Assert.Equal(2 * MB, after.Apps["new.exe"].Disk, 3);
        Assert.Equal(1 * MB, after.Apps["late.exe"].Disk, 3);
    }

    [Fact]
    public void The_processor_share_is_still_worked_out_alongside()
    {
        var sampler = new ProcessSampler();
        Read(sampler, 10, new Proc(100, "busy.exe", 5000, 0, 0, CpuTime: 0, MemBytes: 300 * MB));
        // One second of one core, in the 100 ns steps Windows counts in.
        var next = Read(sampler, 11, new Proc(100, "busy.exe", 5000, 0, 0, CpuTime: TimeSpan.TicksPerSecond, MemBytes: 300 * MB));
        Assert.Equal(100.0 / Environment.ProcessorCount, next.Apps["busy.exe"].Cpu, 3);
        Assert.Equal(300, next.Apps["busy.exe"].MemMB, 3);
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void Writing_a_file_shows_as_disk_use_of_this_process()
    {
        string me = Path.GetFileName(Environment.ProcessPath)!;
        var sampler = new ProcessSampler();
        sampler.Sample();
        string file = Path.Combine(Path.GetTempPath(), $"rigsight-disk-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(file, new byte[8 * MB]);
            Thread.Sleep(200);
            var snapshot = sampler.Sample();
            // 8 MB in well under ten seconds.
            Assert.True(snapshot.Apps[me].Disk > 0.8 * MB, $"{snapshot.Apps[me].Disk:0} bytes a second");
            Assert.True(snapshot.Disk >= snapshot.Apps[me].Disk);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
