using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rigsight;

/// <summary>
/// Entry point. The .NET garbage collector sizes its short-lived allocation area from the CPU's cache, which on
/// large-cache CPUs (a Ryzen X3D has 96 MB of L3) means tens of megabytes held for a UI that needs a few.
/// That size can only be set before the runtime starts, through an environment variable, so the app sets it
/// for itself and starts again (about a tenth of a second), then clears it so nothing it opens inherits it.
/// </summary>
public static partial class Program
{
    private const string Gen0Variable = "DOTNET_GCgen0size";
    private const string Gen0Size = "0x400000"; // 4 MB: garbage collections stay rare, memory stays small

    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable(Gen0Variable) is null && Environment.ProcessPath is { } exe)
        {
            try
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var a in args) start.ArgumentList.Add(a);
                start.Environment[Gen0Variable] = Gen0Size;
                // This process was started by the user, so it may bring a window to the front; pass that on.
                AllowSetForegroundWindow(-1);
                using var child = Process.Start(start);
                if (child is not null) return 0;
            }
            catch
            {
                // Couldn't start again: run as we are (just with the runtime's default GC sizing).
            }
        }
        Environment.SetEnvironmentVariable(Gen0Variable, null);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
