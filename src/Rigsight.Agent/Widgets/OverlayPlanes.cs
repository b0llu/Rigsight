using System.Runtime.InteropServices;
using Rigsight.Core;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Whether a screen has overlay planes for windowed games: DXGI's IDXGIOutput6::CheckHardwareCompositionSupport with
/// its WINDOWED flag, layers the graphics card puts together itself, so a widget over a borderless game is drawn on one
/// of its own and the game keeps going straight to the screen ("Hardware Composed: Independent Flip"), with
/// G-Sync/FreeSync on. Without them Windows would have to compose the game's frames with the widget, so widgets step
/// aside for a borderless game there. Measured with PresentMon on Dota 2 borderless at 240 fps (RTX 3080 Ti,
/// 2026-10-16): no widget, one and three widgets over it, every frame Hardware Composed: Independent Flip, frame times
/// the same; that screen answers flags 3 (fullscreen and windowed). The older IDXGIOutput2::SupportsOverlays said no on
/// the same screen: it isn't the question for this.
/// Asked once a screen (screens are seldom changed; a new arrangement gives new handles, which are asked anew). A
/// screen Windows can't say for counts as without: widgets then step aside, as they always did. UI thread only.
/// </summary>
internal static unsafe class OverlayPlanes
{
    private static readonly Dictionary<IntPtr, bool> Known = [];

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private const uint WindowedComposition = 2; // DXGI_HARDWARE_COMPOSITION_SUPPORT_FLAG_WINDOWED
    private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND

    // Places in the COM method tables (IUnknown's 3 first, then IDXGIObject's 4).
    private const int QueryInterface = 0;
    private const int Release = 2;
    private const int EnumAdapters1 = 12;     // IDXGIFactory1
    private const int EnumOutputs = 7;        // IDXGIAdapter
    private const int GetDesc = 7;            // IDXGIOutput
    private const int CheckHardwareCompositionSupport = 28; // IDXGIOutput6

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    /// <summary>Whether the screen <paramref name="monitor"/> (an HMONITOR) has overlay planes; false when unknown.</summary>
    public static bool On(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return false;
        if (Known.TryGetValue(monitor, out bool known)) return known;
        bool has = false;
        string said;
        try
        {
            (has, said) = Ask(monitor);
        }
        catch (Exception ex)
        {
            said = $"Windows couldn't say ({ex.GetType().Name}: {ex.Message})";
        }
        Known[monitor] = has;
        // Logged once a screen: why widgets stay over a borderless game on this PC, or why they step aside.
        Log.Write("widgets", $"Overlay planes on {said}: {(has ? "yes, widgets can stay over borderless games" : "no, widgets step aside for borderless games")}");
        return has;
    }

    private static (bool Has, string Said) Ask(IntPtr monitor)
    {
        if (CreateDXGIFactory1(IidFactory1, out var factory) < 0 || factory == 0) return (false, "this screen (expected DXGI, found none)");
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(factory, EnumAdapters1);
            for (uint a = 0; a < 16; a++)
            {
                nint adapter;
                int hr = enumAdapters(factory, a, &adapter);
                if (hr == NotFound || hr < 0 || adapter == 0) break;
                try
                {
                    var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(adapter, EnumOutputs);
                    for (uint o = 0; o < 16; o++)
                    {
                        nint output;
                        hr = enumOutputs(adapter, o, &output);
                        if (hr == NotFound || hr < 0 || output == 0) break;
                        try
                        {
                            if (Describe(output) is not { } desc || desc.Monitor != monitor) continue;
                            string name = desc.DeviceName.Trim();
                            var iid = IidOutput6;
                            nint output6;
                            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(output, QueryInterface))(output, &iid, &output6) < 0 || output6 == 0)
                                return (false, $"{name} (expected IDXGIOutput6, Windows older than 10 1803)");
                            try
                            {
                                uint flags;
                                int hr6 = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Method(output6, CheckHardwareCompositionSupport))(output6, &flags);
                                if (hr6 < 0) return (false, $"{name} (expected an answer, Windows said 0x{hr6:X8})");
                                return ((flags & WindowedComposition) != 0, $"{name} (hardware composition flags {flags})");
                            }
                            finally
                            {
                                Free(output6);
                            }
                        }
                        finally
                        {
                            Free(output);
                        }
                    }
                }
                finally
                {
                    Free(adapter);
                }
            }
        }
        finally
        {
            Free(factory);
        }
        return (false, "this screen (expected it among the graphics card's outputs, not found)");
    }

    private static OutputDesc? Describe(nint output)
    {
        var getDesc = (delegate* unmanaged[Stdcall]<nint, nint, int>)Method(output, GetDesc);
        nint buffer = Marshal.AllocHGlobal(Marshal.SizeOf<OutputDesc>());
        try
        {
            return getDesc(output, buffer) < 0 ? null : Marshal.PtrToStructure<OutputDesc>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint Method(nint com, int slot) => (*(nint**)com)[slot];

    private static void Free(nint com)
    {
        if (com != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Method(com, Release))(com);
    }
}
