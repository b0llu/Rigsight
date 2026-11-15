using System.Text;

namespace Rigsight.Core;

/// <summary>
/// A small file people would miss (settings, saved AI keys, conversations, a Rewind, their own AI instructions), written so
/// a power cut or a crash while saving can't leave it empty or half written: the new content goes to a file beside it, is
/// flushed to the disk, and only then swapped in. With a backup, the file it replaces stays as "name.bak" for a read that
/// fails to fall back on.
/// </summary>
public static class SafeFile
{
    public const string BackupSuffix = ".bak";

    /// <summary>The copy the last save kept, for a read that fails to fall back on.</summary>
    public static string BackupOf(string path) => path + BackupSuffix;

    public static void WriteText(string path, string text, bool backup = true) => Write(path, Encoding.UTF8.GetBytes(text), backup);

    public static void Write(string path, byte[] bytes, bool backup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            // On the disk before it takes the old file's place: a rename can reach the disk before the data does.
            stream.Flush(flushToDisk: true);
        }
        if (backup && File.Exists(path))
        {
            try
            {
                File.Replace(tmp, path, BackupOf(path), ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // The copy can't be kept right now (in use): saving matters more.
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
