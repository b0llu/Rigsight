using System.Drawing;
using System.Runtime.InteropServices;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Where Windows 11's taskbar buttons are, for the strip on the taskbar's left end: Start (the first of the centred
/// icons, which the strip must stay clear of) and Widgets (the weather, at the far left when it's on). They are drawn
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

    /// <summary>What was last found: Start (empty when not found) and Widgets (empty when not shown).</summary>
    public sealed record Places(Rectangle Start, Rectangle Widgets);

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
                }
                var now = new Places(start is null ? Rectangle.Empty : Bounds(start), widgets is null ? Rectangle.Empty : Bounds(widgets));
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
    private const int UIA_BoundingRectanglePropertyId = 30001, UIA_AutomationIdPropertyId = 30011;

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

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, [MarshalAs(UnmanagedType.Interface)] object condition);
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
