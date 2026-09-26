namespace Rigsight.Core;

/// <summary>Tiny append-only log file, capped at ~1 MB.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string source, string message) => WriteTo(RigsightPaths.LogFile, source, message);

    /// <summary>Appends to the log at <paramref name="path"/>: <see cref="RigsightPaths.LogFile"/>, or a test's.</summary>
    internal static void WriteTo(string path, string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var file = new FileInfo(path);
                if (file is { Exists: true, Length: > 1_000_000 })
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                var line = System.Text.Encoding.UTF8.GetBytes($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {message}{Environment.NewLine}");
                // The agent and the app share this file. Opened for appending only (no right to write anywhere else),
                // Windows puts every write at the end as it is at that moment: two programs writing at once can't
                // overwrite each other's lines, as they could when each wrote where it found the end on opening. Tried
                // again for a moment if it's busy (being moved to .old, say).
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var stream = new FileInfo(path).Create(FileMode.Append, System.Security.AccessControl.FileSystemRights.AppendData,
                            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None, null);
                        stream.Write(line);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(20);
                    }
                }
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string source, Exception ex) => Write(source, ex.ToString());
}
