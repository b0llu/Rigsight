using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>What a wireless device is, for its icon and its group's badge.</summary>
internal enum DeviceKind { Mouse, Keyboard, Headset, Controller, Other }

/// <summary>
/// One reading of a wireless device's battery, as its source gave it.
/// </summary>
/// <param name="Key">Stable across restarts: the source and the device's own identity ("logitech/046d-c547/1/4099").</param>
/// <param name="Name">The device's own name ("G502 X PLUS"), or Windows' for it.</param>
/// <param name="Percent">0 to 100 when the device gives a percent.</param>
/// <param name="Level">When it only reports a level instead: Full, Medium, Low or Empty (<see cref="Percent"/> null).</param>
/// <param name="Charging">True or false only where the device says; null where it doesn't (Bluetooth).</param>
/// <param name="Connected">False when it didn't answer this time (off, asleep, out of range): the reading is its last.</param>
/// <param name="Via">How it's reached, said on its group: "Logitech receiver", "Bluetooth", "Xbox Wireless".</param>
internal sealed record DeviceBattery(string Key, string Name, DeviceKind Kind, double? Percent, BatteryLevel? Level,
    bool? Charging, bool Connected, string Via)
{
    /// <summary>
    /// What it says about charging, as shown (1.22.0): full on power where the device says so, else charging or not from
    /// <see cref="Charging"/>, unknown where it can't say (Bluetooth, Xbox controllers).
    /// </summary>
    public ChargeState Charge
    {
        get => _charge ?? (Charging switch { true => ChargeState.Charging, false => ChargeState.NotCharging, null => ChargeState.Unknown });
        init => _charge = value;
    }

    private readonly ChargeState? _charge;
}

/// <summary>A level, for the devices that report no percent (an Xbox controller), worst first.</summary>
internal enum BatteryLevel { Empty, Low, Medium, Full }
