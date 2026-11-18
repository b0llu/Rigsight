namespace Rigsight.Core.Net;

/// <summary>
/// The time an app's own small packets take to come back from its server. A call app sends its server a keep-alive every
/// few seconds and the server sends the same packet straight back: Discord, 8 bytes every 5 seconds, back in 104 ms on the
/// user's line (2026-11-17). Timed from the network trace, that's the way the call's voice really goes, and nothing extra
/// is sent. A connection to the server's port 443 can go another way: to Discord's server behind Cloudflare it took 110 and
/// 250 ms in turn while Discord's own packets took 104, and Discord said 245. A packet size counts as an echo only once it
/// has come back steadily: never on its own, and in about the same time each time.
/// </summary>
public sealed class EchoTimer
{
    /// <summary>Keep-alives are small: bigger packets are the call itself.</summary>
    public const int MostBytes = 64;

    /// <summary>An echo not back by then (ms) is lost.</summary>
    public const int WaitMs = 1000;

    /// <summary>Echoes back before a size counts as the app's echo.</summary>
    public const int Found = 3;

    /// <summary>What's kept, in ms.</summary>
    public const int KeptMs = 60_000;

    /// <summary>One packet sent at <see cref="At"/> (ms on the trace's clock), back after <see cref="Ms"/>, or lost (null).</summary>
    public readonly record struct Echo(double At, int Size, int? Ms);

    private readonly Dictionary<int, double> _waiting = [];
    private readonly List<Echo> _echoes = [];
    // Every packet that came in, by size: an echo's size never comes in unasked (a call's voice can have any size).
    private readonly Dictionary<int, List<double>> _in = [];

    /// <summary>One packet with the server, sent or received, at <paramref name="at"/> ms.</summary>
    public void Packet(double at, bool sent, int size)
    {
        Expire(at);
        if (size <= 0 || size > MostBytes) return;
        if (sent)
        {
            // A second one of the same size before the first is back: the first is the one timed.
            _waiting.TryAdd(size, at);
            return;
        }
        if (!_in.TryGetValue(size, out var ins)) _in[size] = ins = [];
        ins.Add(at);
        if (_waiting.Remove(size, out double out_))
            _echoes.Add(new Echo(out_, size, (int)Math.Round(at - out_)));
    }

    /// <summary>Packets still not back <see cref="WaitMs"/> after they went are lost; what's older than kept goes.</summary>
    public void Expire(double now)
    {
        foreach (var (size, at) in _waiting.Where(w => now - w.Value > WaitMs).ToList())
        {
            _waiting.Remove(size);
            _echoes.Add(new Echo(at, size, null));
        }
        _echoes.RemoveAll(e => e.At < now - KeptMs);
        foreach (var ins in _in.Values) ins.RemoveAll(t => t < now - KeptMs);
    }

    /// <summary>
    /// The size the app's echo has, once <see cref="Found"/> came back, none of that size came in unasked (two spare
    /// for one that crossed the window's start), and four in five took within 25% (at least 15 ms) of their middle.
    /// </summary>
    public int? Size()
    {
        int? best = null;
        int most = 0;
        foreach (var g in _echoes.Where(e => e.Ms is not null).GroupBy(e => e.Size))
        {
            var ms = g.Select(e => e.Ms!.Value).OrderBy(x => x).ToList();
            if (ms.Count < Found || ms.Count <= most) continue;
            if (_in.TryGetValue(g.Key, out var ins) && ins.Count > ms.Count + 2) continue;
            double middle = ms[ms.Count / 2], room = Math.Max(15, middle * 0.25);
            if (ms.Count(x => Math.Abs(x - middle) <= room) * 5 < ms.Count * 4) continue;
            (best, most) = (g.Key, ms.Count);
        }
        return best;
    }

    /// <summary>The echoes of <paramref name="size"/> sent in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public List<Echo> Between(int size, double from, double to) => [.. _echoes.Where(e => e.Size == size && e.At >= from && e.At < to)];

    /// <summary>When the last packet of <paramref name="size"/> was sent (null: none kept).</summary>
    public double? LastSent(int size) => _echoes.Where(e => e.Size == size).Select(e => (double?)e.At).Max()
        ?? (_waiting.TryGetValue(size, out double at) ? at : null);
}
