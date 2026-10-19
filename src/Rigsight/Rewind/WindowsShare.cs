using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Rigsight.Core;

namespace Rigsight.Rewind;

/// <summary>
/// Windows' own Share window for pictures and a line of text: picking the X app (or any other) there opens its post
/// with both already in it. An X link can only carry text; Share carries the pictures too.
/// </summary>
public static class WindowsShare
{
    private static (string Title, string Text, IReadOnlyList<string> Files)? _pending;
    private static bool _hooked;

    /// <summary>False where Windows has no Share window for desktop apps (before Windows 10).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    public static bool Show(Window owner, string title, string text, IReadOnlyList<string> files)
    {
        try
        {
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            if (!_hooked)
            {
                manager.DataRequested += OnDataRequested;
                _hooked = true;
            }
            _pending = (title, text, files);
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            Log.Write("rewind", $"Windows Share opened with {files.Count} picture(s)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("rewind", ex);
            return false;
        }
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_pending is not { } p) return;
        var deferral = args.Request.GetDeferral();
        try
        {
            var data = args.Request.Data;
            data.Properties.Title = p.Title;
            data.SetText(p.Text);
            var items = new List<IStorageItem>();
            foreach (var path in p.Files) items.Add(await StorageFile.GetFileFromPathAsync(path));
            data.SetStorageItems(items);
        }
        catch (Exception ex)
        {
            Log.Error("rewind", ex);
            args.Request.FailWithDisplayText(Lang.T("The pictures couldn't be shared. Try Export instead."));
        }
        finally
        {
            deferral.Complete();
        }
    }
}
