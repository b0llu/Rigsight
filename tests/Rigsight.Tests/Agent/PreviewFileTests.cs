using System.Drawing;
using Rigsight.Agent.Widgets;

namespace Rigsight.Tests.Agent;

/// <summary>The app reads widget pictures while the agent may be rewriting them: a busy file never breaks the agent.</summary>
public sealed class PreviewFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rigsight-previews-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Bitmap Picture(Color c)
    {
        var bmp = new Bitmap(4, 4);
        using var g = Graphics.FromImage(bmp);
        g.Clear(c);
        return bmp;
    }

    private static Color Read(string path)
    {
        using var bmp = new Bitmap(new MemoryStream(File.ReadAllBytes(path)));
        return bmp.GetPixel(0, 0);
    }

    [Fact]
    public void A_picture_is_written_and_replaces_the_last_one()
    {
        var path = Path.Combine(_dir, "Pill.png");
        using (var red = Picture(Color.Red)) Assert.True(PreviewFile.Save(red, path));
        using (var blue = Picture(Color.Blue)) Assert.True(PreviewFile.Save(blue, path));
        Assert.Equal(Color.Blue.ToArgb(), Read(path).ToArgb());
    }

    [Fact]
    public void A_file_the_app_is_reading_is_skipped_without_an_error_and_left_whole()
    {
        var path = Path.Combine(_dir, "Pill.png");
        using (var red = Picture(Color.Red)) PreviewFile.Save(red, path);
        // The app holds it open for reading, sharing only reads (as WPF's image loading does).
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var blue = Picture(Color.Blue))
            Assert.False(PreviewFile.Save(blue, path));
        Assert.Equal(Color.Red.ToArgb(), Read(path).ToArgb());
    }

    [Fact]
    public void A_file_freed_while_retrying_is_written()
    {
        var path = Path.Combine(_dir, "Pill.png");
        using (var red = Picture(Color.Red)) PreviewFile.Save(red, path);
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // On a thread of its own: in a full run the shared pool is busy, and a timer on it once fired after the retries had ended.
        new Thread(() =>
        {
            Thread.Sleep(10);
            reader.Dispose();
        }) { IsBackground = true }.Start();
        using (var blue = Picture(Color.Blue))
            Assert.True(PreviewFile.Save(blue, path));
        Assert.Equal(Color.Blue.ToArgb(), Read(path).ToArgb());
    }
}
