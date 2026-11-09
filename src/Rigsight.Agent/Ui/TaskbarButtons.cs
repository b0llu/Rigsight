using System.Drawing;
using System.Runtime.InteropServices;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Where Windows 11's taskbar buttons are, so the readings never cover them: Start (the first of the centred icons, which
/// the left side must stay clear of), Widgets (the weather, at the far left when it's on) and where the apps' buttons
/// end (which the readings by the clock must stay clear of: past it they get narrower, then wait in "+N"). They are drawn
/// by the taskbar itself, with no windows of their own, so only UI Automation knows where they are: Windows' UI
/// Automation through COM, as the managed one would load WPF into the agent.
/// Asked on a thread of its own, never the strip's: the strip is a window inside the taskbar, so UI Automation asks it
/// about itself while looking through the taskbar, and a strip's thread waiting on that answer never gives it (the
/// strip never showed). The buttons are found once and asked where they are every <see cref="Every"/> (one call
/// each), or at once when told something moved (<see cref="Check"/>); found again every <see cref="RefindEvery"/>
/// (Widgets can be switched on or off) or when asking fails. <paramref name="moved"/> is called on that thread when
/// either has moved. <see cref="Watch"/> and <see cref="Stop"/> from the strip's thread only.
/// </summary>
internal sealed class TaskbarButtons(Action moved) : IDisposable
{
    // Slow on its own: the strip asks at once (Check) when the apps' buttons move, which is when Start moves. This is
    // only for a move nobody told of (Widgets switched on or off, a taskbar setting), so an idle PC asks Explorer once
    // in 5 seconds, not twice a second.
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5), RefindEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What was last found: Start (empty when not found), Widgets (empty when not shown) and the right edge of the last
    /// app button, in screen pixels (0 when none was found); and how far before the tray Widgets begins when Windows put it
    /// beside the tray (<see cref="WidgetsBeforeTray"/>; 0 when it didn't), which the readings by the clock keep clear of.
    /// </summary>
    public sealed record Places(Rectangle Start, Rectangle Widgets, int AppsRight = 0, int WidgetsToTray = 0);

    /// <summary>
    /// How far <paramref name="widgets"/> begins before the tray (whose left edge is <paramref name="trayLeft"/>), when
    /// Windows put it beside the tray: with the taskbar's icons aligned left, Widgets (the weather) moves from the bar's
    /// left end to just before the tray's icons, where the readings by the clock go (2026-11-08: a person's readings
    /// were drawn over "Mostly cloudy"). Measured from the tray, it stays right as the tray grows and shrinks between
    /// looks. 0 when Widgets isn't shown, or is at the bar's left end (icons centred: it's before Start).
    /// </summary>
    internal static int WidgetsBeforeTray(Rectangle start, Rectangle widgets, int? trayLeft)
    {
        if (widgets.IsEmpty || trayLeft is not { } tray || widgets.Left >= tray) return 0;
        // Centred icons: Widgets is at the bar's left end, before Start. Without Start, by how near the tray it ends.
        bool byTray = start.IsEmpty ? tray - widgets.Right <= widgets.Height : widgets.Left > start.Left;
        return byTray ? tray - widgets.Left : 0;
    }

    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private int _turn; // each thread asks while it is the latest started (a stopped one ends at its next turn)
    private bool _watching;
    private volatile Places? _latest;
    private IntPtr _taskbar;

    /// <summary>The places last found; null until the first answer for this taskbar.</summary>
    public Places? Latest => _latest;

    /// <summary>Keeps asking where the buttons on <paramref name="taskbar"/> are (a new taskbar: asked afresh).</summary>
    public void Watch(IntPtr taskbar)
    {
        if (taskbar != Interlocked.Exchange(ref _taskbar, taskbar)) _latest = null;
        if (_watching) return;
        _watching = true;
        int turn = Interlocked.Increment(ref _turn);
        _thread = new Thread(() => Run(turn)) { IsBackground = true, Name = "Taskbar buttons" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Asks now, not at the next turn (the apps' buttons moved).</summary>
    public void Check() => _wake.Set();

    /// <summary>Stops asking (the strip went back next to the clock, or away).</summary>
    public void Stop()
    {
        if (!_watching) return;
        _watching = false;
        Interlocked.Increment(ref _turn);
        _wake.Set();
        _thread = null;
        _latest = null;
    }

    private void Run(int turn)
    {
        bool Stopped() => Volatile.Read(ref _turn) != turn;
        IUIAutomation? uia = null;
        IUIAutomationElement? start = null, widgets = null;
        object? appsCondition = null;
        IntPtr foundOn = IntPtr.Zero;
        var found = DateTime.MinValue;
        string? lastError = null;
        while (!Stopped())
        {
            try
            {
                uia ??= (IUIAutomation)new CUIAutomation();
                IntPtr taskbar = _taskbar;
                if (taskbar != foundOn || start is null || DateTime.UtcNow - found > RefindEvery)
                {
                    foundOn = taskbar;
                    found = DateTime.UtcNow;
                    var bar = uia.ElementFromHandle(taskbar);
                    start = bar.FindFirst(TreeScope_Descendants, uia.CreatePropertyCondition(UIA_AutomationIdPropertyId, "StartButton"));
                    widgets = bar.FindFirst(TreeScope_Descendants, uia.CreatePropertyCondition(UIA_AutomationIdPropertyId, "WidgetsButton"));
                    appsCondition = uia.CreatePropertyCondition(UIA_ClassNamePropertyId, AppButtonClass);
                }
                var (startAt, widgetsAt) = (start is null ? Rectangle.Empty : Bounds(start), widgets is null ? Rectangle.Empty : Bounds(widgets));
                var now = new Places(startAt, widgetsAt, AppsRight(uia.ElementFromHandle(taskbar), appsCondition),
                    WidgetsBeforeTray(startAt, widgetsAt, TrayLeft(taskbar)));
                lastError = null;
                if (!Stopped() && taskbar == _taskbar && now != _latest)
                {
                    _latest = now;
                    moved();
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
            {
                // Explorer restarted, or a button was rebuilt: found again on the next turn. Said once, not every half second.
                if (ex.Message != lastError) Rigsight.Core.Log.Write("taskbar", $"Finding the taskbar's buttons failed: {ex.Message}");
                lastError = ex.Message;
                start = widgets = null;
                if (_latest is null && !Stopped()) { _latest = new Places(Rectangle.Empty, Rectangle.Empty); moved(); }
            }
            _wake.WaitOne(Every);
        }
    }

    /// <summary>The apps' buttons, pinned or running (Windows 11's own class name for them, checked on 22H2 and later).</summary>
    private const string AppButtonClass = "Taskbar.TaskListButtonAutomationPeer";

    /// <summary>
    /// Where the last app button ends (screen pixels), 0 with none: asked afresh each turn, as apps come and go. A few
    /// dozen buttons at most, one call each.
    /// </summary>
    private static int AppsRight(IUIAutomationElement bar, object? condition)
    {
        if (condition is null || bar.FindAll(TreeScope_Descendants, condition) is not { } buttons) return 0;
        int right = 0;
        for (int i = 0, n = buttons.Length; i < n; i++)
            if (Bounds(buttons.GetElement(i)) is { IsEmpty: false } b) right = Math.Max(right, b.Right);
        return right;
    }

    /// <summary>The tray's left edge in screen pixels (its window: the notification icons and the clock); null when not found.</summary>
    private static int? TrayLeft(IntPtr taskbar)
    {
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return tray != IntPtr.Zero && GetWindowRect(tray, out var r) ? r.L : null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private static Rectangle Bounds(IUIAutomationElement element)
    {
        // Left, top, width, height, in screen pixels; all zero for a button that isn't shown.
        if (element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is not double[] { Length: 4 } r) return Rectangle.Empty;
        var rect = new Rectangle((int)r[0], (int)r[1], (int)r[2], (int)r[3]);
        return rect.Width <= 0 || rect.Height <= 0 ? Rectangle.Empty : rect;
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    private const int TreeScope_Descendants = 4;
    private const int UIA_BoundingRectanglePropertyId = 30001, UIA_AutomationIdPropertyId = 30011, UIA_ClassNamePropertyId = 30012;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation;

    // Only the calls used; the others are kept as places, for their order in the interface.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        [return: MarshalAs(UnmanagedType.Interface)]
        object CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
        IUIAutomationElementArray? FindAll(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
