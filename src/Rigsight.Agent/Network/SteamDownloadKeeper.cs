using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Games;

namespace Rigsight.Agent.Network;

/// <summary>
/// Saves what Steam downloaded into the history (2026-10-30, the user: "save downloads in history"): Steam keeps its
/// download log in two files and deletes the oldest lines, and forgets a game's name when it's uninstalled; saved here,
/// a download stays named for good. Looked at once every <see cref="Every"/> seconds at most, and only read again when
/// Steam's log has changed; not in the first minutes after the agent starts, which stay quiet.
/// </summary>
internal sealed class SteamDownloadKeeper(RigsightDb db, Func<IReadOnlyList<string>>? logFiles = null, Func<IReadOnlyList<GameDownload>>? read = null)
{
    /// <summary>Seconds between looks at Steam's log.</summary>
    public const long Every = 600;

    /// <summary>Seconds after the agent starts before the first look.</summary>
    public const long FirstAfter = 180;

    private readonly Func<IReadOnlyList<string>> _logFiles = logFiles ?? SteamDownloads.LogFiles;
    private readonly Func<IReadOnlyList<GameDownload>> _read = read ?? SteamDownloads.ReadThisPc;
    private long _started = -1, _last;
    private string? _seen;

    /// <summary>At the minute <paramref name="now"/> (unix seconds): saves Steam's downloads when it's time and its log changed.</summary>
    public void Tick(long now)
    {
        if (_started < 0) { _started = now; return; }
        if (now - _started < FirstAfter || now - _last < Every) return;
        _last = now;
        try
        {
            // Each log file's size and last write together: Steam writing a line changes them.
            string seen = string.Join('|', _logFiles().Select(f => { var i = new FileInfo(f); return $"{f}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
            if (seen.Length == 0 || seen == _seen) return;
            // Only what ended since the history begins: right after the history is cleared (or trimmed), Steam's log
            // still holds months of older downloads, which mustn't come back into it.
            var since = db.FirstDataTime() is long first ? TimeUtil.FromUnix(first) : DateTime.Now;
            var downloads = _read().Where(d => d.Ended >= since).ToList();
            db.SaveSteamDownloads(downloads);
            _seen = seen;
            Log.Write("games", $"Steam's download log read: {downloads.Count} downloads saved");
        }
        catch (Exception ex) { Log.Write("games", $"Steam's downloads couldn't be saved ({ex.GetType().Name}: {ex.Message})"); }
    }
}
