using System.Collections.ObjectModel;
using System.Text.Json;

namespace Rigsight.Models;

/// <summary>
/// Lists a page shows are read again on every visit and every minute, and mostly come back the same. A list given to
/// the page anew has every row of it built and laid out again (a tenth of a second and more on the pages with many):
/// these hand the page the list it already has when nothing in it changed, or change only the rows that did.
/// </summary>
public static class Kept
{
    /// <summary>The list already shown when the fresh one says the same, else the fresh one.</summary>
    public static List<T> Or<T>(List<T> shown, List<T> fresh, Func<T, T, bool>? same = null) => Same(shown, fresh, same) ? shown : fresh;

    /// <inheritdoc cref="Or{T}(List{T}, List{T}, Func{T, T, bool}?)"/>
    public static IReadOnlyList<T> Or<T>(IReadOnlyList<T> shown, IReadOnlyList<T> fresh, Func<T, T, bool>? same = null) => Same(shown, fresh, same) ? shown : fresh;

    private static bool Same<T>(IReadOnlyList<T> shown, IReadOnlyList<T> fresh, Func<T, T, bool>? same)
    {
        if (ReferenceEquals(shown, fresh)) return true;
        if (shown.Count != fresh.Count) return false;
        for (int i = 0; i < shown.Count; i++)
            if (!(same?.Invoke(shown[i], fresh[i]) ?? EqualityComparer<T>.Default.Equals(shown[i], fresh[i]))) return false;
        return true;
    }

    /// <summary>
    /// Makes the list shown say what the fresh one does, row by row: a row that says the same stays as it is (and so
    /// does what the page built for it), one that differs is replaced, and the list grows or shrinks at its end.
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> shown, IReadOnlyList<T> fresh, Func<T, T, bool>? same = null)
    {
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i >= shown.Count) shown.Add(fresh[i]);
            else if (!(same?.Invoke(shown[i], fresh[i]) ?? EqualityComparer<T>.Default.Equals(shown[i], fresh[i]))) shown[i] = fresh[i];
        }
        while (shown.Count > fresh.Count) shown.RemoveAt(shown.Count - 1);
    }

    /// <summary>
    /// Whether two rows of a kind that doesn't compare itself (a class with many figures) say the same: by everything
    /// they'd write out, so a figure added to the kind later is compared too.
    /// </summary>
    public static bool Values<T>(T a, T b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        try { return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b); }
        catch (NotSupportedException) { return false; }
    }
}
