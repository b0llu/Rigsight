using System.Numerics;
using System.Runtime.InteropServices;

namespace Rigsight.Core.Parts;

/// <summary>A physical core as Windows lists it: its efficiency class (higher is faster) and how many threads it runs.</summary>
public readonly record struct CoreTopology(byte EfficiencyClass, int Threads);

/// <summary>
/// The CPU's cores as Windows lists them (GetLogicalProcessorInformationEx, one entry per physical core, in Windows'
/// order): what tells a hybrid CPU's performance cores from its efficient ones where the sensor names don't (1.23.0's CPU
/// page). Read once; it doesn't change while the PC runs.
/// </summary>
public static class CpuTopology
{
    private static IReadOnlyList<CoreTopology>? _cores;

    /// <summary>Every physical core, in Windows' order; empty when Windows wouldn't say.</summary>
    public static IReadOnlyList<CoreTopology> Cores => _cores ??= Read();

    /// <summary>How many logical processors (threads) the cores run together.</summary>
    public static int Threads => Cores.Count > 0 ? Cores.Sum(c => c.Threads) : Environment.ProcessorCount;

    /// <summary>More than one efficiency class: performance and efficient cores.</summary>
    public static bool IsHybrid(IReadOnlyList<CoreTopology> cores) => cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;

    private const int RelationProcessorCore = 0;

    private static List<CoreTopology> Read()
    {
        try
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
            if (length == 0) return [];
            var buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                {
                    Log.Write("cpu", $"Expected Windows' list of CPU cores, found error {Marshal.GetLastWin32Error()}");
                    return [];
                }
                var bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, (int)length);
                var cores = Parse(bytes, (int)length);
                Log.Write("cpu", $"Windows lists {cores.Count} cores, {cores.Sum(c => c.Threads)} threads, efficiency classes {string.Join(",", cores.Select(c => c.EfficiencyClass).Distinct().Order())}");
                return cores;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception ex)
        {
            Log.Write("cpu", $"Expected Windows' list of CPU cores, found {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// The cores in a SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffer: each entry's relationship (4 bytes), size (4), then
    /// its PROCESSOR_RELATIONSHIP: flags (1), efficiency class (1), 20 reserved, the group count (2) and that many
    /// GROUP_AFFINITY (a mask of 8 bytes, the group's number of 2 and 6 reserved) from byte 32.
    /// </summary>
    internal static List<CoreTopology> Parse(byte[] buffer, int length)
    {
        var cores = new List<CoreTopology>();
        int at = 0;
        while (at + 32 <= length)
        {
            int relationship = BitConverter.ToInt32(buffer, at);
            int size = BitConverter.ToInt32(buffer, at + 4);
            if (size <= 0) break;
            if (relationship == RelationProcessorCore)
            {
                byte efficiency = buffer[at + 9];
                int groups = BitConverter.ToUInt16(buffer, at + 30), threads = 0;
                for (int g = 0; g < groups && at + 32 + g * 16 + 8 <= length; g++)
                    threads += BitOperations.PopCount(BitConverter.ToUInt64(buffer, at + 32 + g * 16));
                cores.Add(new CoreTopology(efficiency, Math.Max(1, threads)));
            }
            at += size;
        }
        return cores;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
