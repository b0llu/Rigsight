using System.Windows.Forms;
using Rigsight.Agent.Ui;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Putting the readings' strip in the taskbar says whether it took (2026-11-08: right after signing in Windows refused it,
/// nothing checked, and the strip sat loose at the top of the screen all morning). Tried on windows of its own, never the
/// real taskbar.
/// </summary>
[Collection("Drawing")]
public sealed class TaskbarStripAttachTests
{
    [Fact]
    public void Attaching_says_true_only_when_the_window_is_really_inside()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
                using var child = new Form { ShowInTaskbar = false };
                parent.CreateControl();
                child.CreateControl();
                var parentHandle = parent.Handle;

                Assert.True(TaskbarStrip.MakeChild(child.Handle, parentHandle, out _));

                // A parent that isn't there (a taskbar gone or not made yet): refused, and said.
                using var loose = new Form { ShowInTaskbar = false };
                loose.CreateControl();
                Assert.False(TaskbarStrip.MakeChild(loose.Handle, new IntPtr(0x7FFF_FFF0), out int error));
                Assert.NotEqual(0, error);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
