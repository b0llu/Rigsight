using System.Drawing;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// App icons as bitmaps, the most recently used few only (the app in front, the app a notice is about): an agent that
/// runs for months sees hundreds of apps, and keeping every icon would grow for as long as it runs. Thread-safe; a
/// bitmap handed out stays valid until <see cref="Capacity"/> other icons have been asked for since.
/// </summary>
internal sealed class RecentIcons(int capacity = 16)
{
    // Each icon with its place in the most-recent-first list (paths in any case are the same app).
    private readonly Dictionary<string, (Bitmap? Bitmap, LinkedListNode<string> Node)> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = [];
    private readonly Lock _lock = new();

    public int Capacity { get; } = capacity;

    public int Count
    {
        get { lock (_lock) return _icons.Count; }
    }

    /// <summary>The icon of <paramref name="path"/>, loaded with <paramref name="load"/> the first time (null: none).</summary>
    public Bitmap? Get(string path, Func<string, Bitmap?> load)
    {
        lock (_lock)
        {
            if (_icons.TryGetValue(path, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddFirst(entry.Node);
                return entry.Bitmap;
            }
            var bitmap = load(path);
            _icons[path] = (bitmap, _order.AddFirst(path));
            while (_order.Count > Capacity)
            {
                var oldest = _order.Last!;
                _order.RemoveLast();
                if (_icons.Remove(oldest.Value, out var gone)) gone.Bitmap?.Dispose();
            }
            return bitmap;
        }
    }
}
