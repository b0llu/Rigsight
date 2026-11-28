using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// Logitech's newer wireless headsets on their LIGHTSPEED receiver (PRO X 2 LIGHTSPEED, PRO X 3 LIGHTSPEED, G522
/// LIGHTSPEED). They don't answer the HID++ questions mice and keyboards do (see <see cref="LogitechDevices"/>): their
/// receiver has a port of its own (usage page FFA0) that takes Logitech's newer reports, and passes questions on to the
/// headset through its "bridge" feature (0003). The headset's battery feature (0104) answers with its own percent and
/// whether it's charging, and that's all that's shown. Questions only: nothing is ever set on a headset.
/// Older G headsets (G533, G633, G933, G935, G PRO Wireless, G535) report only a voltage, so they aren't read: a percent
/// from a voltage needs a curve for each model, a guess (2026-11-28 rule: no made-up figures).
/// One thread only (<see cref="DeviceBatteries"/>'s).
/// </summary>
internal sealed class LogitechHeadsets : IDisposable
{
    private const ushort Logitech = 0x046D;
    // Our own number on each question, so another app's questions on the same port (G HUB's) aren't taken for ours.
    private const byte Software = 0x0E;
    // The receiver passes the question on and the headset answers: under a second when it's on.
    private const int AnswerMs = 1500;
    private const ushort BridgeFeature = 0x0003, BatteryFeature = 0x0104;

    /// <summary>A headset known by its receiver's product number: its name, and what starts each of its reports.</summary>
    internal sealed record Model(ushort Product, string Name, byte[] Prefix);

    /// <summary>The headsets whose reports are known (their receivers' numbers).</summary>
    internal static readonly Model[] Known =
    [
        new(0x0AF7, "PRO X 2 LIGHTSPEED", [0x51]),
        new(0x0B18, "G522 LIGHTSPEED", [0x50, 0x23]),
        new(0x0B45, "PRO X 3 LIGHTSPEED", [0x50, 0x23]),
    ];

    /// <summary>
    /// Older G headsets (G930, G533, G633, G635, G933, G935, G733, G431-G433, G535, G PRO and PRO X wireless), known by
    /// their receivers' numbers: they give their battery only as a voltage, so they're named in the log and not read.
    /// Their port (FF43) can't tell them apart: Logitech keyboards have one too.
    /// </summary>
    internal static readonly HashSet<ushort> VoltageOnly =
    [
        0x0A1F, 0x0A66, 0x0A5C, 0x0A89, 0x0A5B, 0x0A87, 0x0AB5, 0x0AFE, 0x0A9B, 0x0A9C, 0x0A6D, 0x0AC4,
        0x0AA7, 0x0AAA, 0x0ABA, 0x0AFB, 0x0AFC,
    ];

    // A receiver on this port we don't know yet: each way of starting a report is tried, the newer one first.
    private static readonly byte[][] Prefixes = [[0x50, 0x23], [0x51]];

    private sealed class Port(Hid.Interface port, Hid.Channel channel) : IDisposable
    {
        public Hid.Interface Info { get; } = port;
        public Hid.Channel Channel { get; } = channel;
        public byte[]? Prefix;
        public byte? Bridge, Battery;
        public string Name = "Logitech headset";
        public long LastLookMs = long.MinValue / 2;
        public DeviceBattery? Last;
        public string LastSaid = "";
        public void Dispose() => Channel.Dispose();
    }

    private readonly List<Port> _ports = [];
    private string _seenPorts = "";

    /// <summary>
    /// Finds the headsets' ports when the list of ports changed, then asks each headset for its battery. A headset that
    /// is off keeps its last reading, as not connected. A receiver that never answered is asked again every 10 minutes.
    /// </summary>
    public List<DeviceBattery> Read(long nowMs)
    {
        var all = Hid.List(v => v == Logitech);
        var found = all.Where(i => i.UsagePage == 0xFFA0 && i.Usage == 0x0001 && i.OutputLength >= 20).ToList();
        string seen = string.Join('|', found.Select(p => p.Path).Order(StringComparer.Ordinal));
        if (seen != _seenPorts)
        {
            _seenPorts = seen;
            Close();
            foreach (var i in found)
            {
                if (Hid.Channel.Open(i) is not { } channel)
                {
                    Log.Write("devices", $"Expected to open Logitech headset port {i.Vendor:x4}:{i.Product:x4} for its battery; Windows wouldn't open it");
                    continue;
                }
                _ports.Add(new Port(i, channel));
            }
            // An older headset: say it was seen, so a log tells why nothing shows for it.
            foreach (var old in all.Select(i => i.Product).Distinct().Where(VoltageOnly.Contains))
                Log.Write("devices", $"Logitech headset {Logitech:x4}:{old:x4} is one that reports only a voltage: not read");
        }

        var list = new List<DeviceBattery>();
        foreach (var p in _ports)
            if (Reading(p, nowMs) is { } reading) list.Add(reading);
        return list;
    }

    private DeviceBattery? Reading(Port p, long nowMs)
    {
        string where = $"{p.Info.Vendor:x4}:{p.Info.Product:x4}";
        if (p.Bridge is null)
        {
            if (nowMs - p.LastLookMs < 600_000) return null;
            p.LastLookMs = nowMs;
            if (!FindBridge(p))
            {
                Log.Write("devices", $"Logitech headset port {where}: expected its receiver to name its bridge (feature 0003); no answer");
                return null;
            }
            Log.Write("devices", $"Logitech headset port {where}: {p.Name}, bridge at {p.Bridge:x2}");
        }

        string said;
        DeviceBattery? reading = null;
        if (p.Battery is null)
        {
            var (index, outcome) = SubFeature(p, BatteryFeature);
            if (index is > 0) p.Battery = index;
            said = p.Battery is null ? $"no battery feature ({outcome})" : "";
        }
        else said = "";
        if (p.Battery is { } battery)
        {
            var (answer, outcome) = AskBridge(p, battery, 0, [], anyAsker: true);
            if (answer is not null && BatteryOf(answer) is var (percent, charge))
            {
                reading = new DeviceBattery($"logitech-headset/{p.Info.Product:x4}", p.Name, DeviceKind.Headset, percent, null,
                    charge == ChargeState.Charging, true, "Logitech receiver") { Charge = charge };
                said = $"{percent:0}%, {charge}, answer {Convert.ToHexString(answer.AsSpan(0, Math.Min(answer.Length, 8)))}";
            }
            else said = answer is null ? $"no battery ({outcome})" : $"a battery answer out of range: {Convert.ToHexString(answer.AsSpan(0, Math.Min(answer.Length, 8)))}";
        }
        // Each change in what it says, so a log shows what the headset answered (its bytes) or why it didn't.
        if (said != p.LastSaid)
        {
            p.LastSaid = said;
            Log.Write("devices", $"Logitech {p.Name} ({where}): {said}");
        }
        if (reading is not null) return p.Last = reading;
        return p.Last is { } last ? last with { Connected = false } : null;
    }

    /// <summary>Asks the receiver where its bridge is, in each way of starting a report until one is answered.</summary>
    private static bool FindBridge(Port p)
    {
        var model = Known.FirstOrDefault(m => m.Product == p.Info.Product);
        if (model is not null) p.Name = model.Name;
        byte[][] tries = model is null ? Prefixes : [model.Prefix];
        foreach (var prefix in tries)
        {
            p.Prefix = prefix;
            var answer = Exchange(p, Direct(prefix, 0x00, 0, [BridgeFeature >> 8, BridgeFeature & 0xFF]),
                a => DirectAnswer(a, prefix, 0x00, 0));
            if (answer.Data is [> 0 and var bridge, ..]) { p.Bridge = bridge; return true; }
        }
        p.Prefix = null;
        return false;
    }

    /// <summary>The place of a feature on the headset (0 when it hasn't it), asked through the bridge.</summary>
    private static (byte? Index, Outcome Outcome) SubFeature(Port p, ushort id)
    {
        var (answer, outcome) = AskBridge(p, 0x00, 0, [(byte)(id >> 8), (byte)id], anyAsker: false);
        return (answer is [var index, ..] ? index : null, outcome);
    }

    private static (byte[]? Answer, Outcome Outcome) AskBridge(Port p, byte feature, byte function, byte[] args, bool anyAsker)
    {
        var a = Exchange(p, BridgeQuestion(p.Prefix!, p.Bridge!.Value, feature, function, args),
            r => BridgeAnswer(r, p.Prefix!, p.Bridge!.Value, feature, function, anyAsker));
        return (a.Data, a.Outcome);
    }

    /// <summary>Sends one report and reads what comes back until <paramref name="judge"/> settles it or time runs out.</summary>
    private static Verdict Exchange(Port p, byte[] frame, Func<byte[], Verdict> judge)
    {
        if (!p.Channel.Send(frame)) return new(Outcome.NotSent);
        using var cts = new CancellationTokenSource(AnswerMs);
        while (true)
        {
            var report = p.Channel.ReceiveAsync(cts.Token).GetAwaiter().GetResult();
            if (report is null) return new(Outcome.NoAnswer);
            var verdict = judge(report);
            if (verdict.Outcome != Outcome.Keep) return verdict;
        }
    }

    // ── Reports (pure, for the tests) ────────────────────────────────────

    /// <summary>What a report coming back meant for the question asked.</summary>
    internal enum Outcome { Keep, Answer, Refused, Rejected, Off, NoAnswer, NotSent }

    internal readonly record struct Verdict(Outcome Outcome, byte[]? Data = null);

    /// <summary>
    /// A report: its start (the report number and, on some receivers, a second byte), the length of what follows plus
    /// one, a flags byte (0: all in one report), then the message.
    /// </summary>
    internal static byte[] Frame(byte[] prefix, ReadOnlySpan<byte> message)
    {
        var frame = new byte[prefix.Length + 2 + message.Length];
        prefix.CopyTo(frame, 0);
        frame[prefix.Length] = (byte)(message.Length + 1);
        message.CopyTo(frame.AsSpan(prefix.Length + 2));
        return frame;
    }

    /// <summary>A question to the receiver itself: the feature's place, the function with our number, its arguments.</summary>
    internal static byte[] Direct(byte[] prefix, byte feature, byte function, ReadOnlySpan<byte> args) =>
        Frame(prefix, [feature, (byte)(function << 4 | Software), .. args]);

    /// <summary>
    /// A question for the headset, passed on by the bridge (its function 1, "send"): the bridge's place, the length of the
    /// headset's message, then that message (0, the feature's place on the headset, the function with our number, its arguments).
    /// </summary>
    internal static byte[] BridgeQuestion(byte[] prefix, byte bridge, byte feature, byte function, ReadOnlySpan<byte> args)
    {
        byte[] inner = [0x00, feature, (byte)(function << 4 | Software), .. args];
        return Frame(prefix, [bridge, 0x10 | Software, (byte)(inner.Length >> 8 & 0x0F), (byte)inner.Length, .. inner]);
    }

    /// <summary>The message inside a report, or null when the report isn't one of these (another start, a bad length).</summary>
    internal static byte[]? Message(byte[] report, byte[] prefix)
    {
        int head = prefix.Length;
        if (report.Length < head + 3 || !report.AsSpan(0, head).SequenceEqual(prefix)) return null;
        int length = report[head];
        if (length <= 1 || head + 1 + length > report.Length) return null;
        return report[(head + 2)..(head + 1 + length)];
    }

    /// <summary>The receiver's answer to a question of ours: the same feature, function and our number; what follows.</summary>
    internal static Verdict DirectAnswer(byte[] report, byte[] prefix, byte feature, byte function)
    {
        if (Message(report, prefix) is not { Length: >= 2 } m) return new(Outcome.Keep);
        if (m[0] == feature && m[1] == (byte)(function << 4 | Software)) return new(Outcome.Answer, m[2..]);
        if (m[0] == 0xFF && m[1] == feature && m.Length >= 3 && m[2] == (byte)(function << 4 | Software)) return new(Outcome.Refused);
        return new(Outcome.Keep);
    }

    /// <summary>
    /// What the bridge sent back for a question passed to the headset. It first says it took the question (its function
    /// 1 with our number), then passes on the headset's answer (function 1, number 0: the length, a 0, then the headset's
    /// own answer: the feature's place, the function and number, what follows). It can also refuse it outright (FF), or
    /// say the headset went off (function 0, number 0, the last byte 0). <paramref name="anyAsker"/>: an answer to the
    /// same question asked by another app is as good (the battery, asked with no arguments); a question with arguments
    /// takes only its own answer.
    /// </summary>
    internal static Verdict BridgeAnswer(byte[] report, byte[] prefix, byte bridge, byte feature, byte function, bool anyAsker)
    {
        if (Message(report, prefix) is not { Length: >= 2 } m) return new(Outcome.Keep);
        if (m.Length >= 3 && m[0] == 0xFF && m[1] == bridge && m[2] == (0x10 | Software)) return new(Outcome.Rejected);
        if (m[0] != bridge) return new(Outcome.Keep);
        if (m[1] == 0x00) return m.Length >= 4 && m[3] == 0 ? new(Outcome.Off) : new(Outcome.Keep);
        if (m[1] >> 4 != 1 || (m[1] & 0x0F) != 0 || m.Length < 7 || m[4] != 0x00) return new(Outcome.Keep); // its "took it", or another message
        if (m[5] == feature && m[6] >> 4 == function && (anyAsker || (m[6] & 0x0F) == Software)) return new(Outcome.Answer, m[7..]);
        if (m[5] == 0xFF && m[6] == feature && m.Length >= 8 && m[7] >> 4 == function && (anyAsker || (m[7] & 0x0F) == Software)) return new(Outcome.Refused);
        return new(Outcome.Keep);
    }

    /// <summary>
    /// The battery feature's answer: the percent first, then a byte not used here, then the charging state (1 or 2
    /// charging). Null for a percent outside 1-100: nothing is shown rather than a figure the headset didn't give.
    /// </summary>
    internal static (double Percent, ChargeState Charge)? BatteryOf(byte[] answer)
    {
        if (answer is not [var percent, ..] || percent is 0 or > 100) return null;
        var charge = answer.Length >= 3 && answer[2] is 1 or 2 ? ChargeState.Charging : ChargeState.NotCharging;
        return (percent, charge);
    }

    private void Close()
    {
        foreach (var p in _ports) p.Dispose();
        _ports.Clear();
    }

    public void Dispose() => Close();
}
