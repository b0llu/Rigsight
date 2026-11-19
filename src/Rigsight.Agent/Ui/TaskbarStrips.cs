using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

/// <summary>
/// The taskbar readings on each taskbar that shows them (<see cref="TrayScreens"/>): the main one's strip, and one strip
/// for each other screen's taskbar while it's asked for, each placed and fitted by its own taskbar (its room, its scale,
/// its own "+N"). Every strip gets the same readings, styles and colours; a click, hover or right-click is answered on the
/// strip it was made on. A screen plugged in gets a strip at the next update, one unplugged loses its strip.
/// UI thread only.
/// </summary>
internal sealed class TaskbarStrips(Func<IntPtr, TaskbarStrip> make) : IDisposable
{
    private readonly TaskbarStrip _main = make(IntPtr.Zero);
    private readonly Dictionary<IntPtr, TaskbarStrip> _others = [];
    private string _note = "";

    /// <summary>The readings waiting for room in "+N" on the main taskbar, or on the first other one while the main shows none.</summary>
    public IReadOnlyList<string> Waiting { get; private set; } = [];

    /// <summary>
    /// Shows the readings on the taskbars <paramref name="screens"/> asks for (the rest as <see cref="TaskbarStrip.Update"/>).
    /// True when the main taskbar's readings are taken care of: its own strip is shown, or they're on other screens'
    /// taskbars as asked. False means the agent shows them as Windows' own tray icons instead (Windows 10, or a main
    /// taskbar that refused the strip), as before.
    /// </summary>
    public bool Update(IReadOnlyList<TrayReading> readings, bool gray, TraySide side, TrayStyle style, bool tiles, TrayWidth width,
        bool letters, bool speedBits, TrayGap spacing, TrayScreens screens)
    {
        // Another screen's taskbar only on Windows 11, where the strip is drawn at all.
        var others = TaskbarStrip.Supported && readings.Count > 0 ? TaskbarScreens.Others() : [];
        bool onMain = TaskbarScreens.OnMain(screens, others.Count), onOthers = TaskbarScreens.OnOthers(screens);
        bool many = others.Count > 0;

        string note = TaskbarScreens.Describe(screens, others.Count);
        if (note != _note)
        {
            Log.Write("taskbar", $"The readings go on {note}" + (others.Count > 0 ? $" (other taskbars: {string.Join(", ", others.Select(h => $"0x{h.ToInt64():X}"))})" : ""));
            _note = note;
        }

        // Strips whose taskbar went (a screen unplugged, Explorer restarted) or isn't asked for any more.
        foreach (var gone in _others.Keys.Where(h => !onOthers || !others.Contains(h)).ToList())
        {
            _others[gone].Dispose();
            _others.Remove(gone);
        }

        bool mainShown = false;
        if (onMain) mainShown = _main.Update(readings, gray, side, style, tiles, width, letters, speedBits, spacing, screens, many);
        else _main.Hide();

        bool otherShown = false;
        IReadOnlyList<string> otherWaiting = [];
        if (onOthers)
            foreach (var taskbar in others)
            {
                if (!_others.TryGetValue(taskbar, out var strip)) _others[taskbar] = strip = make(taskbar);
                if (strip.Update(readings, gray, side, style, tiles, width, letters, speedBits, spacing, screens, many))
                {
                    if (!otherShown) otherWaiting = strip.Waiting;
                    otherShown = true;
                }
            }

        Waiting = mainShown ? _main.Waiting : otherWaiting;
        return onMain ? mainShown : otherShown || others.Count > 0;
    }

    /// <summary>Takes every strip off its taskbar.</summary>
    public void Hide()
    {
        _main.Hide();
        foreach (var strip in _others.Values) strip.Dispose();
        _others.Clear();
        Waiting = [];
    }

    public void Dispose()
    {
        _main.Dispose();
        foreach (var strip in _others.Values) strip.Dispose();
        _others.Clear();
    }
}
