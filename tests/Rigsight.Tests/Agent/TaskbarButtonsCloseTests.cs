using System.Runtime.InteropServices;
using Rigsight.Agent.Ui;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The look-up of the taskbar's buttons ends cleanly when the agent closes (2026-11-17, issue #10 and this PC's own crash
/// on 13 Nov: the agent died inside UI Automation 50 s into a quit after a graphics driver change, the look-up still
/// running). Its thread is waited for before its wake-up goes, and nothing asks again once it's closed.
/// </summary>
public sealed class TaskbarButtonsCloseTests
{
    [Fact]
    public void Closing_waits_for_the_asking_thread_and_nothing_starts_it_again()
    {
        int moved = 0;
        var buttons = new TaskbarButtons(() => Interlocked.Increment(ref moved));
        // No taskbar window: UI Automation says so, and the thread waits for its next turn.
        buttons.Watch(IntPtr.Zero);
        Assert.True(SpinWait.SpinUntil(() => buttons.Latest is not null, TimeSpan.FromSeconds(10)));
        Assert.True(buttons.Asking);

        buttons.Dispose();
        Assert.False(buttons.Asking);

        // A strip shown again during the quit: no new thread, no wake-up on the handle that's gone.
        buttons.Watch(IntPtr.Zero);
        buttons.Check();
        buttons.Stop();
        buttons.Dispose();
        Assert.False(buttons.Asking);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);

    /// <summary>
    /// A button's place is now read as a plain rectangle, from its own place in UI Automation's interface (no copy of an
    /// array out of Explorer's answer, issue #10). That place is checked against the property it replaced, on this PC's
    /// own taskbar: a wrong place would read another value, or worse.
    /// </summary>
    [Fact]
    public void The_rectangle_is_read_from_the_right_place_in_the_interface()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        Assert.SkipWhen(taskbar == IntPtr.Zero, "No taskbar on this PC (a test machine without Explorer)");
        var both = TaskbarButtons.StartBoth(taskbar);
        Assert.SkipWhen(both is null, "This taskbar has no Start button UI Automation can find (Windows 10)");
        Assert.False(both!.Value.Property.IsEmpty);
        Assert.Equal(both.Value.Property, both.Value.Rect);
    }
}
