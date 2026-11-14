using System.Globalization;

namespace Rigsight.Core.Storage;

/// <summary>
/// The one name a physical drive is known by in the history, whatever number Windows gives it this time (the numbers
/// can change between starts): its serial number, or where a drive has none, its model and size.
/// </summary>
public static class DriveKey
{
    /// <summary>The serial, trimmed, when there is one; otherwise "model|size in GB" (rounded).</summary>
    public static string Of(string model, string? serial, ulong totalBytes)
    {
        var s = serial?.Trim();
        if (!string.IsNullOrEmpty(s)) return s;
        return (model ?? "").Trim() + "|" + Math.Round(totalBytes / 1_000_000_000d).ToString(CultureInfo.InvariantCulture);
    }
}
