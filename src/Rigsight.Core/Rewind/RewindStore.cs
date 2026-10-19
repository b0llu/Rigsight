using System.Text.Json;
using System.Text.Json.Serialization;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Rewind;

/// <summary>
/// The rewinds made, one file each in the data folder's "rewinds" (kept until removed from Settings → Rewind), and
/// which are due. Both programs make them: the agent when one finishes (to announce it while RigSight is closed),
/// the app when it opens. The same period gives the same file, so whichever is first, the other finds it made.
/// </summary>
public static class RewindStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly object Gate = new();

    public static string Folder => Path.Combine(RigsightPaths.DataDir, "rewinds");

    public static List<RewindFacts> All()
    {
        var list = new List<RewindFacts>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var file in Directory.GetFiles(Folder, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<RewindFacts>(File.ReadAllText(file), Json) is { Id.Length: > 0 } f) list.Add(f); }
            catch (Exception ex) { Log.Write("rewind", $"Couldn't read {Path.GetFileName(file)}: {ex.Message}"); }
        }
        // Newest first; on one day the longest first (a year above its last quarter and month).
        return [.. list.OrderByDescending(f => f.To).ThenByDescending(f => f.Kind)];
    }

    public static RewindFacts? Get(string id) => All().FirstOrDefault(f => f.Id == id);

    public static void Save(RewindFacts f)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, f.Id + ".json"), temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(f, Json));
            File.Move(temp, path, overwrite: true);
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            try { File.Delete(Path.Combine(Folder, id + ".json")); }
            catch (IOException ex) { Log.Write("rewind", $"Couldn't remove {id}: {ex.Message}"); }
        }
    }

    public static bool Exists(string id) => File.Exists(Path.Combine(Folder, id + ".json"));

    /// <summary>The period of a kind that holds <paramref name="day"/>: a calendar month, quarter, half year or year.</summary>
    public static (DateTime From, DateTime To) PeriodOf(RewindKind kind, DateTime day)
    {
        int months = Months(kind);
        int first = (day.Month - 1) / months * months + 1;
        var from = new DateTime(day.Year, first, 1);
        return (from, from.AddMonths(months));
    }

    /// <summary>
    /// Nothing ends before this is made (the user's ruling, 2026-10-19): the first rewind is October 2026, seen from
    /// 1 November, not a September one on the day the update arrives.
    /// </summary>
    public static readonly DateTime FirstEnd = new(2026, 11, 1);

    public static int Months(RewindKind kind) => kind switch { RewindKind.Month => 1, RewindKind.Quarter => 3, RewindKind.Half => 6, _ => 12 };

    /// <summary>
    /// History must reach back over the whole period for a kind to be made: with three months kept, no six-month or
    /// yearly rewinds. 0 keeps everything.
    /// </summary>
    public static bool HistoryAllows(RewindKind kind, int keepDays) => keepDays <= 0 || keepDays >= Months(kind) * 30;

    /// <summary>
    /// The rewinds finished by <paramref name="now"/> and not made yet: for each kind switched on, the last period of
    /// it that has ended, when the PC was used on enough of its days. Only the last one: nothing is made for the
    /// months before RigSight was first given the feature.
    /// </summary>
    public static List<(RewindKind Kind, DateTime From, DateTime To)> Due(RigsightDb db, RigsightSettings settings, DateTime now)
    {
        var due = new List<(RewindKind, DateTime, DateTime)>();
        foreach (var kind in new[] { RewindKind.Year, RewindKind.Half, RewindKind.Quarter, RewindKind.Month })
        {
            if (!settings.Rewind.Makes(kind) || !HistoryAllows(kind, settings.Tracking.KeepHistoryDays)) continue;
            var (from, to) = PeriodOf(kind, now.Date);
            (from, to) = (from.AddMonths(-Months(kind)), from);
            if (to < FirstEnd) continue;
            if (Exists(RewindBuilder.IdOf(kind, from))) continue;
            if (db.CountDaysUsed(TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)) < RewindBuilder.EnoughDays(kind)) continue;
            due.Add((kind, from, to));
        }
        return due;
    }

    /// <summary>Makes and saves what is due; the biggest of them is the one to announce, the rest go in the list.</summary>
    public static List<RewindFacts> MakeDue(RigsightDb db, RigsightSettings settings, DateTime now)
    {
        var made = new List<RewindFacts>();
        foreach (var (kind, from, to) in Due(db, settings, now))
        {
            try
            {
                var f = RewindBuilder.Build(db, kind, from, to, settings, now);
                // Same day: the biggest one is announced; the others only go in the list.
                f.Announced = made.Count > 0;
                Save(f);
                made.Add(f);
                Log.Write("rewind", $"Made {f.Id}: {f.DaysOn} days used, {f.OnSec / 3600:0} h on, {f.GamingSec / 3600:0} h of games");
            }
            catch (Exception ex) { Log.Error("rewind", ex); }
        }
        return made;
    }

    /// <summary>Made again with today's settings (what goes in, what is left out), in place of the saved one.</summary>
    public static RewindFacts Recreate(RigsightDb db, RigsightSettings settings, RewindFacts old, DateTime now)
    {
        var f = RewindBuilder.Build(db, old.Kind, old.From, old.To, settings, now);
        (f.Seen, f.Announced) = (old.Seen, true);
        Save(f);
        return f;
    }

    /// <summary>The history needed to recreate it is still kept.</summary>
    public static bool CanRecreate(RewindFacts f, int keepDays, DateTime now) => keepDays <= 0 || f.From >= now.Date.AddDays(-keepDays);
}
