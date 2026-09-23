namespace Rigsight.Core;

/// <summary>Tiny append-only log file, capped at ~1 MB.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string source, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(RigsightPaths.DataDir);
                var file = new FileInfo(RigsightPaths.LogFile);
                if (file is { Exists: true, Length: > 1_000_000 })
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(RigsightPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string source, Exception ex) => Write(source, ex.ToString());
}
