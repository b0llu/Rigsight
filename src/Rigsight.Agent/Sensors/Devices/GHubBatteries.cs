using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Rigsight.Core;

namespace Rigsight.Agent.Sensors.Devices;

/// <summary>
/// The battery Logitech G HUB shows for each of its devices, when G HUB is running (2026-11-28, the user's ruling: a
/// Logitech device in G HUB shows G HUB's percent, Logitech's own figure). G HUB's background program answers its own
/// window on a connection only this PC can reach (127.0.0.1:9010); RigSight asks it the same two things the window does:
/// the list of devices, then each one's battery. Questions only: nothing in G HUB is changed. Not documented by
/// Logitech, so an update of G HUB can change it: then nothing comes from here and the log says why.
/// One thread only (<see cref="DeviceBatteries"/>'s).
/// </summary>
internal sealed class GHubBatteries
{
    private static readonly Uri Address = new("ws://127.0.0.1:9010");
    private const int AnswerMs = 3000;

    /// <summary>One device's battery as G HUB has it.</summary>
    /// <param name="Name">G HUB's name for it ("G309 LIGHTSPEED"), the same the device gives.</param>
    /// <param name="OwnCharge">Whether the device measures its own charge (G HUB works the others out itself).</param>
    internal sealed record Item(string Id, string Name, string LongName, DeviceKind Kind, double Percent, bool Charging, bool Full, bool OwnCharge, bool Active);

    /// <summary>A device in G HUB's list that says it has a battery.</summary>
    internal sealed record Listed(string Id, string Name, string LongName, DeviceKind Kind, bool Active);

    private string _lastSaid = "";

    /// <summary>Every device G HUB has a battery for; null when G HUB isn't running or didn't answer.</summary>
    public List<Item>? Read()
    {
        if (Process.GetProcessesByName("lghub_agent").Length == 0) { Say("G HUB isn't running"); return null; }
        try
        {
            using var cts = new CancellationTokenSource(10_000);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "file://");
            ws.Options.AddSubProtocol("json");
            ws.ConnectAsync(Address, cts.Token).GetAwaiter().GetResult();
            var list = Ask(ws, "/devices/list", cts.Token) is { } l ? Devices(l) : null;
            if (list is null) { Say("G HUB is running; expected its device list, got none"); return null; }
            var items = new List<Item>();
            var said = new List<string>();
            foreach (var d in list)
            {
                if (Ask(ws, $"/battery/{d.Id}/state", cts.Token) is not { } answer || Battery(answer) is not var (percent, charging, full, own))
                {
                    said.Add($"{d.Name}: no battery answer");
                    continue;
                }
                items.Add(new Item(d.Id, d.Name, d.LongName, d.Kind, percent, charging, full, own, d.Active));
                said.Add($"{d.Name} {percent:0}%{(full ? " full" : charging ? " charging" : "")}{(own ? "" : " (G HUB's estimate)")}{(d.Active ? "" : " (not active)")}");
            }
            try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token).GetAwaiter().GetResult(); } catch { }
            Say("G HUB: " + (said.Count == 0 ? "no device with a battery" : string.Join(", ", said)));
            return items;
        }
        catch (Exception ex)
        {
            Say($"G HUB is running; expected to read its batteries on {Address}, failed: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>Logs what G HUB gave each time it changes, so a log tells where a device's figure came from.</summary>
    private void Say(string what)
    {
        if (what == _lastSaid) return;
        _lastSaid = what;
        Log.Write("devices", what);
    }

    /// <summary>Asks one thing and returns G HUB's answer to it (other messages it sends meanwhile are passed over).</summary>
    private static JsonElement? Ask(ClientWebSocket ws, string path, CancellationToken cancel)
    {
        var q = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { msgId = "", verb = "GET", path }));
        ws.SendAsync(q, WebSocketMessageType.Text, true, cancel).GetAwaiter().GetResult();
        long until = Environment.TickCount64 + AnswerMs;
        var buffer = new byte[64 * 1024];
        while (Environment.TickCount64 < until)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult r;
            do
            {
                r = ws.ReceiveAsync(buffer.AsMemory(), cancel).AsTask().GetAwaiter().GetResult();
                if (r.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, r.Count);
                if (message.Length > 16 * 1024 * 1024) return null;
            } while (!r.EndOfMessage);
            using var doc = JsonDocument.Parse(message.ToArray());
            var root = doc.RootElement;
            if (root.TryGetProperty("path", out var p) && p.GetString() == path && root.TryGetProperty("verb", out var v) && v.GetString() == "GET")
                return root.Clone();
        }
        return null;
    }

    // ── Answers (pure, for the tests) ────────────────────────────────────

    /// <summary>The devices in G HUB's list that have a battery. Null when the answer isn't a list.</summary>
    internal static List<Listed>? Devices(JsonElement answer)
    {
        if (!Succeeded(answer) || !answer.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("deviceInfos", out var infos) || infos.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Listed>();
        foreach (var d in infos.EnumerateArray())
        {
            if (!(d.TryGetProperty("capabilities", out var caps) && caps.TryGetProperty("hasBatteryStatus", out var has) && has.ValueKind == JsonValueKind.True)) continue;
            string? id = Text(d, "id");
            string? name = Text(d, "displayName") ?? Text(d, "extendedDisplayName");
            if (id is null || name is null) continue;
            list.Add(new Listed(id, Log.Printable(name), Log.Printable(Text(d, "extendedDisplayName") ?? name), KindOf(Text(d, "deviceType")),
                Text(d, "state") is null or "ACTIVE"));
        }
        return list;
    }

    /// <summary>
    /// A device's battery answer: its percent, charging, full, and whether the device measures its own charge
    /// ("STATE_OF_CHARGE_SUPPORTED"). Null for an answer that failed or a percent outside 1-100.
    /// </summary>
    internal static (double Percent, bool Charging, bool Full, bool OwnCharge)? Battery(JsonElement answer)
    {
        if (!Succeeded(answer) || !answer.TryGetProperty("payload", out var p)) return null;
        if (!p.TryGetProperty("percentage", out var pc) || !pc.TryGetDouble(out double percent) || percent is <= 0 or > 100) return null;
        bool full = p.TryGetProperty("fullyCharged", out var f) && f.ValueKind == JsonValueKind.True;
        bool charging = p.TryGetProperty("charging", out var c) && c.ValueKind == JsonValueKind.True;
        bool own = Text(p, "batteryStateOfChargeSupport") == "STATE_OF_CHARGE_SUPPORTED";
        return (percent, charging, full, own);
    }

    internal static DeviceKind KindOf(string? type) => type switch
    {
        "MOUSE" => DeviceKind.Mouse,
        "KEYBOARD" => DeviceKind.Keyboard,
        "HEADSET" => DeviceKind.Headset,
        "GAMEPAD" or "CONTROLLER" or "WHEEL" => DeviceKind.Controller,
        _ => DeviceKind.Other,
    };

    /// <summary>
    /// Whether a device RigSight reads itself is this one in G HUB: the same name ("G309 LIGHTSPEED"), or G HUB's long
    /// name that is it plus only what kind of device it is ("G309 LIGHTSPEED Wireless Gaming Mouse"). "G309" isn't
    /// "G309 LIGHTSPEED", and "G502" isn't "G502 X PLUS".
    /// </summary>
    internal static bool Same(string ownName, Item g)
    {
        if (string.Equals(ownName, g.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(ownName, g.LongName, StringComparison.OrdinalIgnoreCase)) return true;
        if (!g.LongName.StartsWith(ownName + " ", StringComparison.OrdinalIgnoreCase)) return false;
        return g.LongName[(ownName.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => KindWords.Contains(w));
    }

    // The words G HUB's long names add after a device's own name.
    private static readonly HashSet<string> KindWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wireless", "Gaming", "Mouse", "Headset", "Keyboard", "RGB", "Mechanical", "Controller", "Racing", "Wheel", "Earbuds", "Speaker",
    };

    private static bool Succeeded(JsonElement answer) =>
        answer.TryGetProperty("result", out var r) && Text(r, "code") == "SUCCESS";

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
