using System.Diagnostics;
using Microsoft.Win32;

namespace Rigsight.Core.Stability;

/// <summary>What a driver file is, in the words the file itself carries ("Realtek USB Audio Driver").</summary>
public static class DriverFiles
{
    /// <summary>
    /// Windows' own core, which a blue screen names when it can't tell whose fault it was: naming it explains nothing.
    /// </summary>
    public static bool IsWindowsCore(string? file) => file?.ToLowerInvariant() is "ntoskrnl.exe" or "ntkrnlmp.exe" or "ntkrnlpa.exe" or "hal.dll"
        or "win32k.sys" or "win32kbase.sys" or "win32kfull.sys" or "ntfs.sys" or "fltmgr.sys" or "ci.dll" or "clipsp.sys";

    /// <summary>
    /// The description inside the driver's file, or null (the driver was removed since, or the file says nothing).
    /// Read when the crash is first seen, so it is the driver as it was then.
    /// </summary>
    public static string? Describe(string file)
    {
        try
        {
            if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Find(file) is not { } path) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return Clean(info.FileDescription, info.CompanyName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Where the driver's file is: Windows' drivers folder, or wherever the installed driver of that file name runs
    /// from (most of them sit in the driver store, each in a folder of its own).
    /// </summary>
    private static string? Find(string file)
    {
        string direct = Path.Combine(Environment.SystemDirectory, "drivers", file);
        if (File.Exists(direct)) return direct;
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return null;
        foreach (var name in services.GetSubKeyNames())
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("ImagePath") is not string image || !image.EndsWith("\\" + file, StringComparison.OrdinalIgnoreCase)) continue;
            string path = Resolve(image);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>A driver's path as the registry writes it ("\SystemRoot\System32\…", "System32\drivers\…", "\??\C:\…") as a path that can be opened.</summary>
    internal static string Resolve(string image)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (image.StartsWith(@"\??\", StringComparison.Ordinal)) return image[4..];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return Path.Combine(windows, image[12..]);
        return Path.IsPathRooted(image) && !image.StartsWith('\\') ? image : Path.Combine(windows, image.TrimStart('\\'));
    }

    /// <summary>
    /// The file's description without its trademark marks, with the maker's name in front when the description doesn't
    /// carry it ("Driver" alone says nothing).
    /// </summary>
    internal static string? Clean(string? description, string? company)
    {
        description = Marks(description);
        // "…, Version 617.42": the file as it is now, which for a crash read back from months ago is not the one that failed.
        if (description?.IndexOf(", Version", StringComparison.OrdinalIgnoreCase) is int cut and > 0) description = description[..cut];
        if (string.IsNullOrEmpty(description)) return null;
        company = ChangeLogReader.Shorten(Marks(company) ?? "").Replace(" Semiconductor", "").Trim();
        string first = company.Split(' ')[0];
        return first.Length > 1 && !description.Contains(first, StringComparison.OrdinalIgnoreCase) ? $"{company} {description}" : description;
    }

    private static string? Marks(string? s)
    {
        if (s is null) return null;
        foreach (var mark in new[] { "(r)", "(R)", "(tm)", "(TM)", "®", "™" }) s = s.Replace(mark, "");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
