using Rigsight.Agent.Sensors;

namespace Rigsight.Tests.Agent;

/// <summary>Every sensor gets its own id, even when the library hands out the same one twice.</summary>
public class SensorIdTests
{
    [Fact]
    public void Ids_that_dont_clash_stay_as_they_are()
    {
        string[] ids = ["/gpu-nvidia/0/load/0", "/gpu-nvidia/0/load/3", "/gpu-nvidia/0/temperature/0"];
        Assert.Equal(ids, SensorHost.UniqueIds(ids, ["GPU Core", "GPU Bus", "GPU Core"]));
    }

    [Fact]
    public void Nvidia_bus_and_memory_load_both_get_new_ids()
    {
        // LibreHardwareMonitor 0.9.6 gives "GPU Bus" and "GPU Memory" both load/3. Neither keeps the old id,
        // so a name, hidden flag or range saved under it (which could have been either) matches nothing.
        var ids = SensorHost.UniqueIds(
            ["/gpu-nvidia/0/load/0", "/gpu-nvidia/0/load/3", "/gpu-nvidia/0/load/3"],
            ["GPU Core", "GPU Bus", "GPU Memory"]);

        Assert.Equal(["/gpu-nvidia/0/load/0", "/gpu-nvidia/0/load/3/gpu-bus", "/gpu-nvidia/0/load/3/gpu-memory"], ids);
    }

    [Fact]
    public void Same_id_and_same_name_still_get_distinct_ids()
    {
        var ids = SensorHost.UniqueIds(
            ["/gpu-nvidia/0/voltage/0", "/gpu-nvidia/0/voltage/0", "/gpu-nvidia/0/voltage/0"],
            ["12VHPWR Pin 1", "12VHPWR Pin 1", "GPU Core Voltage"]);

        Assert.Equal(["/gpu-nvidia/0/voltage/0/12vhpwr-pin-1", "/gpu-nvidia/0/voltage/0/12vhpwr-pin-1-2", "/gpu-nvidia/0/voltage/0/gpu-core-voltage"], ids);
    }
}
