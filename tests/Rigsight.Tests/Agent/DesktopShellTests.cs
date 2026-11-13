using Rigsight.Agent.Native;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The agent, run as administrator, has the desktop's own Explorer open the window (and a widget's shortcuts, and
/// RivaTuner), so they run with normal rights. explorer.exe "&lt;program&gt;" did the same 280-650 ms slower on every open
/// from the tray (measured 2026-11-12). The interfaces are declared by hand, and one method out of its place would
/// call another: the way to Explorer is walked here, opening nothing.
/// </summary>
public sealed class DesktopShellTests
{
    [Fact]
    public void The_desktops_Explorer_can_be_asked()
    {
        Assert.True(DesktopShell.CanAsk(out var why), why);
        Assert.Null(why);
    }
}
