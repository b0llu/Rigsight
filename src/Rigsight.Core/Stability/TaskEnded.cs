using System.Globalization;

namespace Rigsight.Core.Stability;

/// <summary>
/// An app or a process ended from the Processes page, as a line on the Timeline. The window ends it itself and then
/// tells the agent (the "task-ended" command), which only writes the line down: the agent is never asked to end anything.
/// </summary>
public static class TaskEnded
{
    public const string Command = "task-ended";

    /// <summary>The command's argument: exe, memory held in MB, and what the page called it (last: a name may hold a "|").</summary>
    public static string Format(string name, string exe, double memMB) =>
        $"{exe}|{Math.Max(0, memMB).ToString("0.#", CultureInfo.InvariantCulture)}|{(name.Length > MaxName ? name[..MaxName] : name)}";

    /// <summary>The longest name kept (a browser tab's title can run on).</summary>
    private const int MaxName = 200;

    /// <summary>The line for an argument made by <see cref="Format"/>; null for anything else.</summary>
    public static SystemChange? Parse(string? arg, DateTime at)
    {
        if (arg?.Split('|', 3) is not [var exe, var mem, var name]) return null;
        exe = exe.Trim();
        name = name.Trim();
        if (exe.Length is 0 or > 260 || name.Length is 0 or > MaxName) return null;
        if (!double.TryParse(mem, NumberStyles.Float, CultureInfo.InvariantCulture, out double memMB) || !double.IsFinite(memMB) || memMB < 0) return null;
        return new SystemChange(at, ChangeKind.TaskEnded, $"{name} ended from Processes")
        {
            Subject = exe,
            Now = memMB >= 1 ? Units.Megabytes(memMB) : null,
        };
    }
}
