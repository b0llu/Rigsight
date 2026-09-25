using Rigsight.Agent.Ui;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>What happened to a notice during an exclusive-fullscreen game.</summary>
internal enum InGame
{
    /// <summary>Not in an exclusive-fullscreen game: the usual card shows.</summary>
    NotNeeded,
    /// <summary>Drawn inside the game by RivaTuner.</summary>
    Shown,
    /// <summary>Nothing can reach the screen (RivaTuner isn't drawing in the game).</summary>
    Unreachable,
}

/// <summary>
/// A notice drawn inside an exclusive-fullscreen game by RivaTuner, where no window can appear. It looks like the
/// notification card and goes where the cards go, bottom right, unless the overlay is showing there: then top right.
/// It has its own RivaTuner slot, so it never moves or changes the overlay. UI thread only.
/// </summary>
internal sealed class InGameNotice : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public InGameNotice()
    {
        Rtss.Clear(Rtss.NoticeOwner); // left behind by a run that didn't exit cleanly
        _timer.Tick += (_, _) => Hide();
    }

    /// <summary>Shows the notice for <paramref name="seconds"/> (replacing one still showing). False if RivaTuner isn't running.</summary>
    public bool Show(Notice notice, OverlaySettings overlay, bool overlayVisible, int seconds)
    {
        bool top = overlayVisible && overlay.Corner == OverlayCorner.BottomRight;
        string text = WidgetRenderer.RtssNoticeText(ToastRenderer.KindLabel(notice.Kind), ToastRenderer.Accent(notice.Kind),
            notice.Title, notice.Body, top, overlay.Scale);
        if (!Rtss.Show(text, Rtss.NoticeOwner)) return false;
        _timer.Stop();
        _timer.Interval = Math.Clamp(seconds, 3, 60) * 1000;
        _timer.Start();
        return true;
    }

    public void Hide()
    {
        _timer.Stop();
        Rtss.Clear(Rtss.NoticeOwner);
    }

    public void Dispose()
    {
        Hide();
        _timer.Dispose();
    }
}
