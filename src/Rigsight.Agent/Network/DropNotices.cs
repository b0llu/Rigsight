using Rigsight.Core;
using Rigsight.Core.Data;

namespace Rigsight.Agent.Network;

/// <summary>
/// When to tell the person the internet dropped and came back (mock-up BWEhGhSQvDsiPkBQ8Tn6M6, 2026-11-05):
/// <list type="bullet">
/// <item>"Down" once a drop has lasted <see cref="AfterSeconds"/> (Windows' own check flickers; a drop under 10 s never
/// counts, see <see cref="ConnectionWatch"/>).</item>
/// <item>"Back" only after a "down" that was said, with how long it lasted.</item>
/// <item>The <see cref="KeepsFrom"/>th drop within <see cref="WindowSeconds"/> says "keeps dropping" once instead, and
/// nothing more is said until <see cref="WindowSeconds"/> pass with no drop.</item>
/// </list>
/// Sampler thread only; returns what to say, the caller shows it.
/// </summary>
internal sealed class DropNotices
{
    public const int AfterSeconds = 15;
    public const int WindowSeconds = 600;
    public const int KeepsFrom = 3;

    /// <summary>The start of the drop going on whose "down" was said (null: none said).</summary>
    private long? _saidDown;
    /// <summary>The starts of the drops in the last <see cref="WindowSeconds"/> (each counted once), with their kinds.</summary>
    private readonly List<(long Start, NetDropKind Kind)> _recent = [];
    /// <summary>"Keeps dropping" was said: quiet until a whole window passes with no drop.</summary>
    private bool _keeps;
    private long _lastDropEnd;

    /// <summary>A notice: its title and words, and the moment it's about (the drop's start, for the page it opens).</summary>
    public sealed record Said(string Title, string Body, bool Good, long At);

    /// <param name="open">The drop going on (null: the internet is there).</param>
    /// <param name="ended">A drop that just ended and counts.</param>
    /// <param name="card">The PC's network card's kind ("Wi-Fi", "Ethernet").</param>
    /// <param name="router">Whether the router answered the line check in the last seconds (null: not checked).</param>
    public Said? Tick(long now, (long Start, NetDropKind Kind)? open, NetDrop? ended, string? card, bool? router)
    {
        _recent.RemoveAll(d => d.Start < now - WindowSeconds);
        if (_keeps && open is null && ended is null && now - _lastDropEnd >= WindowSeconds) _keeps = false;

        if (ended is { } e)
        {
            _lastDropEnd = now;
            Count(e.Start, e.Kind);
            bool wasSaid = _saidDown == e.Start;
            _saidDown = null;
            if (wasSaid && !_keeps)
            {
                var (t, b) = DropWords.Back(e.End - e.Start);
                return new Said(t, b, true, e.Start);
            }
            return null;
        }

        if (open is not { } o || now - o.Start < AfterSeconds || _saidDown == o.Start || _keeps) return null;
        _saidDown = o.Start;
        Count(o.Start, o.Kind);
        if (_recent.Count >= KeepsFrom)
        {
            _keeps = true;
            var kind = _recent.Count(d => d.Kind == NetDropKind.Link) * 2 >= _recent.Count ? NetDropKind.Link : NetDropKind.Internet;
            var (t, b) = DropWords.Keeps(_recent.Count, kind, card);
            return new Said(t, b, false, o.Start);
        }
        var (title, body) = DropWords.Down(o.Kind, card, router);
        return new Said(title, body, false, o.Start);
    }

    private void Count(long start, NetDropKind kind)
    {
        if (!_recent.Any(d => d.Start == start)) _recent.Add((start, kind));
    }
}
