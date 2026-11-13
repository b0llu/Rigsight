using System.Reflection;
using System.Runtime.InteropServices;

namespace Rigsight.Agent.Native;

/// <summary>
/// Opens a program, file, folder or web address the way a double-click on the desktop would: the desktop's own
/// Explorer is asked to open it, so it runs with the person's normal rights even though the agent runs as
/// administrator. This is the method Microsoft's Raymond Chen describes for exactly that ("How can I launch an
/// unelevated process from my elevated process", The Old New Thing, 2013).
/// Why not explorer.exe "&lt;program&gt;" as before: that starts a new explorer.exe, which hands the job to the desktop's
/// own and quits; measured on the owner's PC as administrator (2026-11-12), 290-1,150 ms before the program started,
/// against 11-13 ms asked this way (the first ask in a process too), and what it started ran with normal rights.
/// Every open of the window from the tray, a notification or a widget waited that long.
/// </summary>
internal static class DesktopShell
{
    /// <summary>
    /// Asks the desktop's Explorer to open <paramref name="target"/> (with <paramref name="arguments"/> for a program).
    /// False, with why in <paramref name="failure"/>, when it couldn't be asked: no desktop Explorer (another shell, or it
    /// crashed), or Windows said no. The caller then opens it the old way.
    /// </summary>
    public static bool Open(string target, string? arguments, out string? failure)
    {
        try
        {
            if (Shell(out failure) is not { } shell) return false;
            // Its default action, as a double-click (no "open" named), shown as a window normally is.
            shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell,
                [target, arguments ?? "", Path.GetDirectoryName(target) is { Length: > 0 } folder && File.Exists(target) ? folder : "", Missing.Value, SwShowNormal]);
            return true;
        }
        catch (Exception ex) when (Expected(ex))
        {
            failure = Said(ex);
            return false;
        }
    }

    /// <summary>Whether the desktop's Explorer can be asked (for the tests: nothing is opened).</summary>
    internal static bool CanAsk(out string? failure) => Shell(out failure) is not null;

    /// <summary>The desktop Explorer's Shell.Application, the one whose ShellExecute runs in Explorer itself; null with why.</summary>
    private static object? Shell(out string? failure)
    {
        failure = null;
        try
        {
            if (Win32.GetShellWindow() == IntPtr.Zero) { failure = "no desktop Explorer is running"; return null; }
            var windows = (IShellWindows)new ShellWindowsClass();
            object location = 0 /* CSIDL_DESKTOP */, root = null!;
            if (windows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch) is not { } desktop)
            {
                failure = "the desktop's Explorer window wasn't found";
                return null;
            }
            var service = SidTopLevelBrowser;
            var browserId = typeof(IShellBrowser).GUID;
            var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(ref service, ref browserId);
            var dispatchId = IidDispatch;
            object folderView = browser.QueryActiveShellView().GetItemObject(SvgioBackground, ref dispatchId);
            return folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
        }
        catch (Exception ex) when (Expected(ex))
        {
            failure = Said(ex);
            return null;
        }
    }

    private static bool Expected(Exception ex) =>
        ex is COMException or InvalidCastException or TargetInvocationException or MissingMethodException or InvalidComObjectException or NotSupportedException or UnauthorizedAccessException;

    private static string Said(Exception ex) => $"{ex.GetType().Name}: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}";

    private const int SwcDesktop = 8, SwfoNeedDispatch = 1, SwShowNormal = 1;
    private const uint SvgioBackground = 0;
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindowsClass { }

    // Only the methods up to the one called are named; the order is the interfaces' own (their place in the table is what counts).
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object dispatch, int hwnd, int swClass);
        int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enter);
        void InsertMenusSB(IntPtr shared, IntPtr widths);
        void SetMenuSB(IntPtr shared, IntPtr oleMenu, IntPtr active);
        void RemoveMenusSB(IntPtr shared);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(int enable);
        void TranslateAcceleratorSB(IntPtr msg, short id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint msg, IntPtr w, IntPtr l, out IntPtr result);
        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enter);
        void TranslateAccelerator(IntPtr msg);
        void EnableModeless(int enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rect, out IntPtr hwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr add, IntPtr param);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        [return: MarshalAs(UnmanagedType.IDispatch)] object GetItemObject(uint item, ref Guid riid);
    }
}
