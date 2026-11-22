namespace Rigsight.Agent.Frames;

/// <summary>A swap chain: the process presenting through it and the chain's address (0: presents seen only in the kernel).</summary>
internal readonly record struct ChainKey(int Pid, ulong Chain);

/// <summary>What a kernel-side present event says (see <see cref="PresentTimes.Kernel"/>).</summary>
internal enum KernelPresent
{
    /// <summary>A flip to the screen (Flip_Info, FlipMultiPlaneOverlay_Info): one present each.</summary>
    Flip,
    /// <summary>A copy to the screen (Blit_Info): a present, which a present history event of the same call may follow.</summary>
    Blit,
    /// <summary>A present handed to the desktop's compositor (PresentHistory_Start, PresentHistoryDetailed_Start), by its token.</summary>
    History,
    /// <summary>The end of the kernel's part of a present call (Present_Info).</summary>
    End,
}

/// <summary>
/// The time between one present of a game and the next ("MsBetweenPresents", as Intel PresentMon says it), per swap chain,
/// from the present events of Windows' graphics runtimes and kernel: what RivaTuner's frame times are, measured where the
/// game hands each frame over, so it doesn't depend on the display (tearing, VRR or v-sync alike). A game presents
/// through DXGI (DirectX 10 to 12) or Direct3D 9, whose present calls each say their swap chain; one that presents only
/// through the kernel (Vulkan and OpenGL, mostly) is counted from the kernel's events instead, the way PresentMon counts
/// its "Other" runtime. Fed by the trace's thread (under the caller's lock), taken once a second by the sampler.
/// </summary>
internal sealed class PresentTimes(long frequency, long startQpc)
{
    /// <summary>A process with a DXGI or Direct3D 9 present this recently (seconds) has its kernel events left out: they are those presents' own.</summary>
    internal const double RuntimeHoldSec = 2;

    /// <summary>Kernel events in the trace's first second are left out: whether the process presents through DXGI isn't known yet.</summary>
    internal const double KernelWaitSec = 1;

    /// <summary>Asked less often than this (seconds), what came between isn't joined to what came before (RivaTuner's rule too).</summary>
    internal const double GapSec = 2.5;

    /// <summary>A chain that presented nothing for this long (seconds) is forgotten.</summary>
    private const double ForgetSec = 10;

    /// <summary>Presents kept per chain between two asks, at most; past it the ask says frames went by.</summary>
    internal const int MaxPending = 30_000;

    /// <summary>
    /// A chain's presents since the last ask, by when each began (the precise clock), and the last one taken before.
    /// Kept as times and put in order when taken: events can reach the reader out of order (a present whose call
    /// returned later, a thread that moved to another core).
    /// </summary>
    private sealed class Chain
    {
        public long Last;
        public readonly List<long> Pending = [];
        public readonly List<uint> Frames = [];
        public bool Overflow;
    }

    private readonly Dictionary<ChainKey, Chain> _chains = [];
    /// <summary>A runtime present call under way, by its thread: counted when it returns, unless it failed.</summary>
    private readonly Dictionary<int, (ChainKey Key, long Qpc)> _calls = [];
    /// <summary>Each process's last DXGI or Direct3D 9 present.</summary>
    private readonly Dictionary<int, long> _runtimeAt = [];
    /// <summary>The kernel present each thread is in, and the present history token it has (0: none yet).</summary>
    private readonly Dictionary<int, (bool Open, ulong Token)> _kernel = [];
    private readonly long _hold = (long)(RuntimeHoldSec * frequency), _wait = (long)(KernelWaitSec * frequency);
    private long _takenAt;
    private ChainKey? _picked;

    /// <summary>Presents counted since the start (every chain).</summary>
    public long Presents { get; private set; }

    /// <summary>The chain the last ask took its frames from.</summary>
    public ChainKey? Picked => _picked;

    /// <summary>A DXGI or Direct3D 9 present call began (a test present, DXGI_PRESENT_TEST, is never passed here).</summary>
    public void RuntimeStart(int pid, int tid, ulong chain, long qpc)
    {
        // The call before it on this thread never said it returned (its event lost): it is counted as it stood.
        if (_calls.Remove(tid, out var open)) Count(open.Key, open.Qpc);
        _calls[tid] = (new ChainKey(pid, chain), qpc);
        _runtimeAt[pid] = qpc;
        if (_calls.Count > 256) _calls.Clear();
    }

    /// <summary>A DXGI or Direct3D 9 present call returned: counted at its start unless it failed (a "still drawing" answer: nothing was presented).</summary>
    public void RuntimeStop(int tid, int result)
    {
        if (!_calls.Remove(tid, out var call)) return;
        if (result >= 0) Count(call.Key, call.Qpc);
    }

    /// <summary>
    /// A kernel present event of a process with no DXGI or Direct3D 9 presents: a flip or a copy is a present of its own;
    /// a present history token joins the one the thread is in when that has none yet (a copy's own token comes after it),
    /// and the same token twice is one present (both kinds of history event can come for one call).
    /// </summary>
    public void Kernel(KernelPresent kind, int pid, int tid, ulong token, long qpc)
    {
        if (kind == KernelPresent.End)
        {
            _kernel.Remove(tid);
            return;
        }
        if (qpc - startQpc < _wait) return;
        if (_runtimeAt.TryGetValue(pid, out long at) && qpc - at < _hold) return;
        _kernel.TryGetValue(tid, out var thread);
        if (kind == KernelPresent.History)
        {
            if (thread.Open && (thread.Token == 0 || thread.Token == token))
            {
                _kernel[tid] = (true, token);
                return;
            }
            _kernel[tid] = (true, token);
        }
        else
        {
            _kernel[tid] = (true, 0);
        }
        if (_kernel.Count > 256) _kernel.Clear();
        Count(new ChainKey(pid, 0), qpc);
    }

    private void Count(ChainKey key, long qpc)
    {
        if (!_chains.TryGetValue(key, out var chain)) _chains[key] = chain = new Chain();
        if (chain.Pending.Count >= MaxPending)
        {
            chain.Pending.Clear();
            chain.Overflow = true;
        }
        chain.Pending.Add(qpc);
        Presents++;
    }

    /// <summary>
    /// The frame times of the busiest chain since the last ask (µs, oldest first, into <paramref name="into"/>), the one
    /// taken last time when two are as busy, and when its last present was (the precise clock). Missed says what came
    /// before doesn't join on: the first ask, an ask after <see cref="GapSec"/> or more, another chain than last time, or
    /// more presents than were kept. Every chain's presents are taken, used or not. A present that comes in after a later
    /// one of its chain was taken is left out (its time is already in that frame).
    /// </summary>
    public bool Take(List<uint> into, long nowQpc, out long lastQpc)
    {
        into.Clear();
        lastQpc = 0;
        bool missed = _takenAt == 0 || nowQpc - _takenAt > GapSec * frequency;
        _takenAt = nowQpc;

        ChainKey? best = null;
        int most = 0;
        foreach (var (key, chain) in _chains)
        {
            chain.Frames.Clear();
            chain.Pending.Sort();
            foreach (long t in chain.Pending)
            {
                if (t <= chain.Last) continue;
                if (chain.Last != 0) chain.Frames.Add((uint)Math.Min(uint.MaxValue, (t - chain.Last) * 1_000_000 / frequency));
                chain.Last = t;
            }
            chain.Pending.Clear();
            int n = chain.Frames.Count;
            if (n > most || n == most && n > 0 && key == _picked) { best = key; most = n; }
        }
        if (best is { } pick)
        {
            var chain = _chains[pick];
            if (_picked is { } before && before != pick) missed = true;
            if (chain.Overflow) missed = true;
            _picked = pick;
            if (!missed) into.AddRange(chain.Frames);
            lastQpc = chain.Last;
        }

        long forget = (long)(ForgetSec * frequency);
        List<ChainKey>? gone = null;
        foreach (var (key, chain) in _chains)
        {
            chain.Frames.Clear();
            chain.Overflow = false;
            if (nowQpc - chain.Last > forget) (gone ??= []).Add(key);
        }
        if (gone is not null)
            foreach (var key in gone)
            {
                _chains.Remove(key);
                if (key == _picked) _picked = null;
            }
        return missed;
    }

    /// <summary>
    /// The processes to watch for a game (at most <paramref name="max"/>, Windows' limit for a process filter): the game,
    /// then the processes it started and theirs (a game whose window and drawing are in different processes), each
    /// started after its parent (a parent's ID handed on to a later process isn't its child).
    /// </summary>
    internal static List<int> Watched(int game, IReadOnlyList<(int Pid, int Parent, long Created)> processes, int max = 8)
    {
        var list = new List<int> { game };
        var created = new Dictionary<int, long>();
        foreach (var p in processes) created[p.Pid] = p.Created;
        for (int i = 0; i < list.Count && list.Count < max; i++)
        {
            int parent = list[i];
            long born = created.GetValueOrDefault(parent);
            foreach (var p in processes.Where(p => p.Parent == parent && p.Pid != parent && p.Pid != 0 && p.Created >= born).OrderBy(p => p.Created))
            {
                if (list.Count >= max) break;
                if (!list.Contains(p.Pid)) list.Add(p.Pid);
            }
        }
        return list;
    }
}
