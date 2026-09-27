using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors;

/// <summary>
/// RAM usage ("Total Memory") and Windows' commit ("Virtual Memory") from the moment the agent starts. The library
/// only makes them together with the RAM sticks, whose detection probes the memory bus for seconds (4.4 s here), so
/// the agent opens that later in the background (see <see cref="SensorHost.OpenMemory"/>) and uses these meanwhile:
/// the library's own classes (internal, hence reflection), so names, identifiers and readings are the same, just asking
/// Windows. Empty if a library update renamed them (then RAM usage waits for the full memory group, as before).
/// </summary>
internal static class EarlyMemory
{
    internal static readonly string[] TypeNames =
        ["LibreHardwareMonitor.Hardware.Memory.VirtualMemory", "LibreHardwareMonitor.Hardware.Memory.TotalMemory"];

    public static List<IHardware> Create()
    {
        var list = new List<IHardware>();
        foreach (var name in TypeNames)
        {
            try
            {
                if (typeof(Computer).Assembly.GetType(name) is { } type
                    && Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [new NoSettings()], null) is IHardware hw)
                    list.Add(hw);
            }
            catch (Exception ex)
            {
                Log.Error("sensors", ex);
            }
        }
        return list;
    }

    /// <summary>Nothing remembered: the library keeps sensor names and ranges here, which Rigsight keeps itself.</summary>
    private sealed class NoSettings : ISettings
    {
        public bool Contains(string name) => false;
        public void SetValue(string name, string value) { }
        public string GetValue(string name, string value) => value;
        public void Remove(string name) { }
    }
}
