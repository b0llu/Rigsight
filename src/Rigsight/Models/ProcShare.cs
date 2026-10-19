using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>
/// One part of an opened app's memory on the Processes page: its tabs, its extensions, or the app itself (everything
/// that runs the rest: the main process, graphics, network, helpers). A piece of the bar over the list and an entry
/// under it; each process below carries the same colour.
/// </summary>
public sealed record ProcShare(string Name, double MemMB, string Brush) : IBarPart
{
    public double Weight => MemMB;
    public string Text => Units.Megabytes(MemMB);

    private const string Pages = "CpuBrush", Extensions = "PurpleBrush", Itself = "FaintBrush";

    /// <summary>The colour (a theme brush's name) of a process of this kind.</summary>
    public static string BrushOf(string? kind) => kind switch
    {
        "Tab" or "Window" => Pages,
        "Extension" => Extensions,
        _ => Itself,
    };

    /// <summary>
    /// The split of an app's memory, in a set order; empty unless its processes are of two parts or more (an app of
    /// one kind of process, or one that isn't built of kinds, has nothing to split).
    /// </summary>
    public static IReadOnlyList<ProcShare> Of(string appName, IReadOnlyList<ProcChild> processes)
    {
        if (processes.Count == 0 || processes.Any(p => p.Kind is null)) return [];
        var parts = new List<ProcShare>();
        void Add(Func<ProcChild, bool> of, Func<int, string> name, string brush)
        {
            var these = processes.Where(of).ToList();
            if (these.Count > 0) parts.Add(new ProcShare(name(these.Count), these.Sum(p => p.MemMB), brush));
        }
        Add(p => p.Kind == "Tab", n => n == 1 ? Lang.T("1 tab") : Lang.T($"{n} tabs"), Pages);
        Add(p => p.Kind == "Window", n => n == 1 ? Lang.T("1 window") : Lang.T($"{n} windows"), Pages);
        Add(p => p.Kind == "Extension", n => n == 1 ? Lang.T("1 extension") : Lang.T($"{n} extensions"), Extensions);
        Add(p => BrushOf(p.Kind) == Itself, _ => Lang.T($"{appName} itself"), Itself);
        return parts.Count < 2 ? [] : parts;
    }
}
