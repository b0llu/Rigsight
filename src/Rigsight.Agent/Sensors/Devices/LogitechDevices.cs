using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// Logitech mice, keyboards and pads on a Logitech receiver (LIGHTSPEED, Unifying, Bolt) or on their cable, read the
/// way Logitech documents for its own devices (HID++ 2.0): the device's name and kind, then its battery from the
/// Unified Battery feature (a percent, and whether it's charging) or the older Battery Status one. Questions only:
/// nothing is ever set on a device. A device that reports only a voltage (some older gaming mice) isn't read, since
/// turning a voltage into a percent needs a curve for each model.
/// One thread only (<see cref="DeviceBatteries"/>'s).
/// </summary>
internal sealed class LogitechDevices : IDisposable
{
    private const ushort Logitech = 0x046D;
    // Our own number on each question, so the answers to G HUB's questions (which come to us too) aren't taken for ours.
    private const byte Software = 0x0E;
    // A first question can take most of a second: the receiver wakes its link to the device.
    private const int AnswerMs = 1500;

    private sealed class Device
    {
        public required string Key, Name;
        public required DeviceKind Kind;
        public required byte Index, BatteryFeature;
        public required ushort BatteryType;
        public DeviceBattery? Last;
    }

    /// <summary>A receiver's (or a device's) two HID++ ports: long reports for questions and answers, short ones for its "nothing there".</summary>
    private sealed class Receiver(Hid.Interface port, Hid.Channel channel, Hid.Channel? shortChannel) : IDisposable
    {
        public Hid.Interface Port { get; } = port;
        public Hid.Channel Channel { get; } = channel;
        public Hid.Channel? Short { get; } = shortChannel;
        public void Dispose() { Channel.Dispose(); Short?.Dispose(); }
        public List<Device> Devices { get; } = [];
        public HashSet<byte> Silent { get; } = [];
    }

    private readonly List<Receiver> _receivers = [];
    private string _seenPorts = "";
    private long _lastLookMs = long.MinValue / 2;

    /// <summary>
    /// Finds Logitech HID++ ports when the list of ports changed, and every 10 minutes asks again on the places that
    /// gave no answer (a mouse switched on later). Then reads every device found. Returns their batteries.
    /// </summary>
    public List<DeviceBattery> Read(long nowMs)
    {
        var all = Hid.List(v => v == Logitech);
        var ports = all.Where(i => i.UsagePage == 0xFF00 && i.Usage == 0x0002 && i.OutputLength >= 20).ToList();
        string seen = string.Join('|', ports.Select(p => p.Path).Order(StringComparer.Ordinal));
        if (seen != _seenPorts)
        {
            _seenPorts = seen;
            Close();
            foreach (var port in ports)
            {
                if (Hid.Channel.Open(port) is not { } channel)
                {
                    Log.Write("devices", $"Expected to open Logitech port {port.Vendor:x4}:{port.Product:x4} for its devices' batteries; Windows wouldn't open it");
                    continue;
                }
                // Its short-report port, the same interface's other collection ("...&col01" beside "...&col02").
                var shortPort = all.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x0001 && SameInterface(i.Path, port.Path));
                var receiver = new Receiver(port, channel, shortPort is null ? null : Hid.Channel.Open(shortPort));
                _receivers.Add(receiver);
                Look(receiver, [0xFF, 1, 2, 3, 4, 5, 6]);
            }
            _lastLookMs = nowMs;
        }
        else if (nowMs - _lastLookMs >= 600_000)
        {
            _lastLookMs = nowMs;
            foreach (var r in _receivers.Where(r => r.Silent.Count > 0)) Look(r, [.. r.Silent]);
        }

        var list = new List<DeviceBattery>();
        foreach (var r in _receivers)
            foreach (var d in r.Devices)
            {
                var reading = Battery(r, d);
                if (reading is null && d.Last is { } last) reading = last with { Connected = false };
                if (reading is null) continue;
                if (reading.Connected) d.Last = reading;
                list.Add(reading);
            }
        return list;
    }

    /// <summary>Asks each place on the port for a device, and keeps the ones that answer with a battery.</summary>
    private void Look(Receiver r, byte[] places)
    {
        foreach (byte index in places)
        {
            r.Silent.Remove(index);
            if (r.Devices.Any(d => d.Index == index)) continue;
            // The protocol version: a device answers 2.0 or later; an empty place, a receiver itself, or an asleep device doesn't.
            var ping = Ask(r, index, 0x00, 1, [0, 0, 0xAA]);
            if (ping is null || ping[4] < 2)
            {
                if (index != 0xFF) r.Silent.Add(index);
                continue;
            }
            var (name, kind) = NameAndKind(r, index);
            if (kind is null) continue; // the receiver itself
            byte unified = Feature(r, index, 0x1004), status = 0, voltage = 0;
            if (unified == 0) status = Feature(r, index, 0x1000);
            if (unified == 0 && status == 0) voltage = Feature(r, index, 0x1001);
            string where = $"{r.Port.Vendor:x4}:{r.Port.Product:x4} place {index}";
            if (unified == 0 && status == 0)
            {
                Log.Write("devices", $"Logitech {name} ({where}): expected a battery feature (1004 or 1000); found "
                    + (voltage != 0 ? "only a voltage (1001), not read" : "none"));
                continue;
            }
            var device = new Device
            {
                Key = $"logitech/{r.Port.Product:x4}/{index}/{name}", Name = name, Kind = kind.Value, Index = index,
                BatteryFeature = unified != 0 ? unified : status, BatteryType = unified != 0 ? (ushort)0x1004 : (ushort)0x1000,
            };
            r.Devices.Add(device);
            Log.Write("devices", $"Logitech {name} ({kind}, {where}): battery from feature {device.BatteryType:x4}");
        }
    }

    private static (string Name, DeviceKind? Kind) NameAndKind(Receiver channel, byte index)
    {
        string name = "Logitech device";
        DeviceKind? kind = DeviceKind.Other;
        byte feature = Feature(channel, index, 0x0005);
        if (feature == 0) return (name, kind);
        if (Ask(channel, index, feature, 0, []) is { } count && count[4] is > 0 and <= 64 and var length)
        {
            var chars = new List<byte>();
            while (chars.Count < length && Ask(channel, index, feature, 1, [(byte)chars.Count]) is { } part)
            {
                int take = Math.Min(16, length - chars.Count);
                chars.AddRange(part.AsSpan(4, take).ToArray());
                if (take == 0) break;
            }
            string text = System.Text.Encoding.UTF8.GetString([.. chars]).TrimEnd('\0', ' ');
            if (text.Length > 0) name = Log.Printable(text);
        }
        if (Ask(channel, index, feature, 2, []) is { } type)
            kind = type[4] switch
            {
                0 or 2 => DeviceKind.Keyboard,
                3 or 4 or 5 => DeviceKind.Mouse,
                7 => null,
                8 => DeviceKind.Headset,
                10 or 11 or 12 => DeviceKind.Controller,
                _ => DeviceKind.Other,
            };
        return (name, kind);
    }

    private DeviceBattery? Battery(Receiver r, Device d)
    {
        var a = Ask(r, d.Index, d.BatteryFeature, d.BatteryType == 0x1004 ? (byte)1 : (byte)0, []);
        if (a is null) return null;
        var reading = d.BatteryType == 0x1004 ? Unified(a[4], a[5], a[6], a[7]) : Status(a[4], a[6]);
        if (reading is not var (percent, level, charging)) return null;
        // Full on power where it says so (1.22.0: "Full" after 100%), else charging or not.
        var charge = d.BatteryType == 0x1004 ? DeviceCharge.FromUnified(a[6], a[7]) : DeviceCharge.FromStatus(a[6]);
        return new DeviceBattery(d.Key, d.Name, d.Kind, percent, level, charging, true, "Logitech receiver") { Charge = charge };
    }

    /// <summary>
    /// Unified Battery's status: the charge in percent (0 when the device only gives a level), the level's flags
    /// (8 full, 4 good, 2 low, 1 critical), the charging state (0 discharging, 1 charging, 2 charging slowly, 3 full on
    /// power, 4 a charging fault) and whether outside power is there.
    /// </summary>
    internal static (double? Percent, BatteryLevel? Level, bool? Charging)? Unified(byte percent, byte flags, byte state, byte power)
    {
        bool charging = state is 1 or 2 or 3 || power != 0;
        if (percent is > 0 and <= 100) return (percent, null, charging);
        BatteryLevel? level = (flags & 8) != 0 ? BatteryLevel.Full : (flags & 4) != 0 ? BatteryLevel.Medium
            : (flags & 3) != 0 ? BatteryLevel.Low : null;
        return level is null ? null : (null, level, charging);
    }

    /// <summary>Battery Status's level in percent and its state (0 discharging, 1 charging, 2 almost full, 3 full, 4 charging slowly, 5-6 faults).</summary>
    internal static (double? Percent, BatteryLevel? Level, bool? Charging)? Status(byte percent, byte state) =>
        percent is > 0 and <= 100 ? (percent, null, state is 1 or 2 or 3 or 4) : null;

    /// <summary>The place of a feature on the device (0 when it hasn't it).</summary>
    private static byte Feature(Receiver channel, byte index, ushort id) =>
        Ask(channel, index, 0x00, 0, [(byte)(id >> 8), (byte)id]) is { } a ? a[4] : (byte)0;

    /// <summary>
    /// One question, as a long report; its answer (the whole report), or null when the device says it can't, the
    /// receiver says nothing is there (a short "8F" report), or nothing comes in time (off, asleep, out of range).
    /// </summary>
    private static byte[]? Ask(Receiver r, byte index, byte feature, byte function, ReadOnlySpan<byte> args)
    {
        var q = new byte[20];
        q[0] = 0x11; q[1] = index; q[2] = feature; q[3] = (byte)(function << 4 | Software);
        args.CopyTo(q.AsSpan(4));
        if (!r.Channel.Send(q)) return null;
        long started = Environment.TickCount64;
        try { return Wait(r, q, index, feature); }
        finally { Trace?.Invoke($"{index:x2} {feature:x2} {function}: {Environment.TickCount64 - started} ms"); }
    }

    /// <summary>For a probe run by hand: each question and how long its answer took.</summary>
    internal static Action<string>? Trace;

    private static byte[]? Wait(Receiver r, byte[] q, byte index, byte feature)
    {
        using var cts = new CancellationTokenSource(AnswerMs);
        var longRead = r.Channel.ReceiveAsync(cts.Token);
        var shortRead = r.Short?.ReceiveAsync(cts.Token);
        try
        {
            while (true)
            {
                var done = shortRead is null ? longRead : Task.WhenAny(longRead, shortRead).GetAwaiter().GetResult();
                if (done.GetAwaiter().GetResult() is not { } a) return null; // out of time
                if (done == longRead)
                {
                    if (a.Length >= 20 && a[0] == 0x11 && a[1] == index)
                    {
                        if (a[2] == feature && a[3] == q[3]) return a;
                        if (a[2] == 0xFF && a[3] == feature && a[4] == q[3]) return null; // the device's "can't"
                    }
                    longRead = r.Channel.ReceiveAsync(cts.Token);
                }
                else
                {
                    if (a.Length >= 5 && a[0] == 0x10 && a[1] == index && a[2] == 0x8F && a[3] == feature && a[4] == q[3]) return null;
                    shortRead = r.Short!.ReceiveAsync(cts.Token);
                }
            }
        }
        finally
        {
            // The reads still waiting end here, before the next question starts its own.
            cts.Cancel();
            longRead.Wait(500);
            shortRead?.Wait(500);
        }
    }

    /// <summary>
    /// Whether two collections' paths are the same interface: they differ only in "colNN" and the collection's number at
    /// the end of the instance ("...&amp;mi_02&amp;col02#8&amp;38b98d31&amp;0&amp;0001#" beside "...&amp;col01#8&amp;38b98d31&amp;0&amp;0000#").
    /// </summary>
    internal static bool SameInterface(string a, string b)
    {
        static string Strip(string p) => System.Text.RegularExpressions.Regex.Replace(p.ToLowerInvariant(), @"&col[0-9a-f]{2}#(.*)&[0-9a-f]{4}#", "#$1#");
        return Strip(a) == Strip(b);
    }

    private void Close()
    {
        foreach (var r in _receivers) r.Dispose();
        _receivers.Clear();
    }

    public void Dispose() => Close();
}
