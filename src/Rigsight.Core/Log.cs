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
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string source, Exception ex) => Write(source, ex.ToString());
}
