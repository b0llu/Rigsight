using Rigsight.Core;

namespace Rigsight.Services;

/// <summary>Opens a web page in the person's own browser (checkout, Lemon Squeezy's card and receipts pages).</summary>
public static class Browser
{
    /// <summary>Only https links open: anything else is turned down and logged.</summary>
    public static bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Write("browser", $"Expected an https link to open, got \"{(url.Length > 80 ? url[..80] + "…" : url)}\"");
            return false;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("browser", ex);
            return false;
        }
    }
}
