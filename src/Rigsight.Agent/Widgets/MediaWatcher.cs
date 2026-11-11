using System.Drawing;
using System.Drawing.Drawing2D;
using Rigsight.Core;
using Windows.Media.Control;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// What's playing on the PC, for the Media tile (widgets part two, approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): Windows'
/// own media controls (the ones in the volume flyout), so it works with any player that shows there (Spotify, a browser,
/// the Media Player). Started only while a widget that is on has a Media tile, and stopped when none has.
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly Lock _lock = new();
    private int _version;
    private Bitmap? _oldCover;
    private bool _disposed;

    /// <summary>What's playing now; null: nothing (or not read yet).</summary>
    public MediaNow? Now { get; private set; }

    public static MediaWatcher Start()
    {
        var w = new MediaWatcher();
        _ = w.StartAsync();
        return w;
    }

    private async Task StartAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_lock)
            {
                if (_disposed) return;
                _manager = manager;
                manager.CurrentSessionChanged += (_, _) => Follow();
            }
            Follow();
            Log.Write("widgets", "Media: reading what's playing");
        }
        catch (Exception ex)
        {
            Log.Write("widgets", $"Media: Windows' media controls couldn't be read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Watches the session Windows has as the current one now.</summary>
    private void Follow()
    {
        GlobalSystemMediaTransportControlsSession? session;
        lock (_lock)
        {
            if (_disposed || _manager is null) return;
            session = _manager.GetCurrentSession();
            if (_session is { } old)
            {
                old.MediaPropertiesChanged -= OnProps;
                old.PlaybackInfoChanged -= OnPlayback;
                old.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = session;
            if (session is not null)
            {
                session.MediaPropertiesChanged += OnProps;
                session.PlaybackInfoChanged += OnPlayback;
                session.TimelinePropertiesChanged += OnTimeline;
            }
        }
        _ = ReadAsync(session);
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadAsync(s);

    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = ReadAsync(s, keepCover: true);

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = ReadAsync(s, keepCover: true);

    /// <summary>Where the track is and how long it is, as the player last said (none when it doesn't say).</summary>
    private static (TimeSpan Position, TimeSpan Length, DateTime At) Timeline(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var t = s.GetTimelineProperties();
            var length = t.EndTime - t.StartTime;
            if (length <= TimeSpan.Zero) return default;
            var at = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime.LocalDateTime : DateTime.Now;
            return (t.Position - t.StartTime, length, at);
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>The player's name from Windows' id for it ("Spotify.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic").</summary>
    internal static string AppName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string s = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.Split('.')[^1];
        return s.ToLowerInvariant() switch
        {
            "zunemusic" or "mediaplayer" => "Media Player",
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "vlc" => "VLC",
            "brave" => "Brave",
            "opera" => "Opera",
            _ => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s,
        };
    }

    private async Task ReadAsync(GlobalSystemMediaTransportControlsSession? s, bool keepCover = false)
    {
        try
        {
            if (s is null)
            {
                Set(null);
                return;
            }
            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool prev = info.Controls.IsPreviousEnabled, next = info.Controls.IsNextEnabled;
            var (position, length, at) = Timeline(s);
            if (keepCover && Now is { } now)
            {
                Set(now with { Playing = playing, CanPrevious = prev, CanNext = next, Position = position, Length = length, At = at });
                return;
            }
            var props = await s.TryGetMediaPropertiesAsync();
            Bitmap? cover = null;
            if (props.Thumbnail is { } thumb)
            {
                try
                {
                    using var stream = (await thumb.OpenReadAsync()).AsStream();
                    using var image = Image.FromStream(stream);
                    cover = Small(image, 160);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.ExternalException)
                {
                    cover = null;
                }
            }
            Set(new MediaNow(props.Title ?? "", props.Artist ?? "", playing, cover, prev, next, 0, AppName(s.SourceAppUserModelId), position, length, at));
        }
        catch (Exception ex)
        {
            Log.Write("widgets", $"Media: what's playing couldn't be read ({ex.GetType().Name})");
        }
    }

    private static Bitmap Small(Image image, int side)
    {
        float scale = Math.Min(1f, side / (float)Math.Max(image.Width, image.Height));
        var bmp = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    private void Set(MediaNow? now)
    {
        lock (_lock)
        {
            if (_disposed) { now?.Cover?.Dispose(); return; }
            var before = Now;
            if (now is not null) now = now with { Version = ++_version };
            // A cover handed out may still be drawn once more: the one before last is let go, not the last one.
            if (before?.Cover is { } c && !ReferenceEquals(c, now?.Cover))
            {
                _oldCover?.Dispose();
                _oldCover = c;
            }
            if (before?.Title != now?.Title || before?.Playing != now?.Playing)
                Log.Write("widgets", now is null ? "Media: nothing playing" : $"Media: {(now.Playing ? "playing" : "paused")}, {now.Title.Length} characters of title");
            Now = now;
        }
    }

    /// <summary>Play or pause, the next track, or the one before (a click on the Media tile on the desktop).</summary>
    public void Press(string what)
    {
        var s = _session;
        if (s is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                bool done = what switch
                {
                    WidgetRenderer.HitPlay => await s.TryTogglePlayPauseAsync(),
                    WidgetRenderer.HitNext => await s.TrySkipNextAsync(),
                    WidgetRenderer.HitPrevious => await s.TrySkipPreviousAsync(),
                    _ => false,
                };
                if (!done) Log.Write("widgets", $"Media: the player didn't take {what}");
            }
            catch (Exception ex)
            {
                Log.Write("widgets", $"Media: {what} failed ({ex.GetType().Name})");
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            if (_session is { } s)
            {
                s.MediaPropertiesChanged -= OnProps;
                s.PlaybackInfoChanged -= OnPlayback;
                s.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = null;
            _manager = null;
            Now = null;
        }
    }
}
