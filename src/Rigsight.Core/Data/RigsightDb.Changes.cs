using Rigsight.Core.Stability;

namespace Rigsight.Core.Data;

// What changed on the PC (0.15.0): drivers and Windows updates from the event logs, kept here because Windows' logs
// roll over, and everything found by setting the PC's inventory against the last one (apps, startup programs,
// hardware, settings). The inventory table is that last reading.
public sealed partial class RigsightDb
{
    private const string InventoryKindsKey = "inventory_kinds", InventoryOwnerKey = "inventory_owner", ChangesScannedKey = "changes_scanned";

    private void EnsureChangesSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS changes(id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, kind TEXT NOT NULL, subject TEXT NOT NULL,
                title TEXT NOT NULL, was TEXT, now TEXT, UNIQUE(kind, ts, subject));
            CREATE INDEX IF NOT EXISTS ix_changes_ts ON changes(ts);
            CREATE TABLE IF NOT EXISTS inventory(kind TEXT NOT NULL, key TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
                PRIMARY KEY(kind, key)) WITHOUT ROWID;
            """);
        // When the check before ran (0.15.1): a change found by a check happened between then and its time.
        if (!HasColumn("changes", "from_ts")) Exec("ALTER TABLE changes ADD COLUMN from_ts INTEGER");
    }

    // The app reads a database the agent made, which may be from before 0.15.0.
    private bool? _hasChanges, _hasChangeFrom;
    public bool HasChanges => _hasChanges ??= HasTable("changes");
    private bool HasChangeFrom => _hasChangeFrom ??= HasColumn("changes", "from_ts");

    // ── Writer ────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores changes; ones already recorded are ignored. A driver counts once per version: VPN and virtual adapters
    /// are "installed" again every time they're created. Returns the newly added ones.
    /// </summary>
    public List<SystemChange> InsertChanges(IEnumerable<SystemChange> changes)
    {
        var added = new List<SystemChange>();
        foreach (var c in changes.OrderBy(c => c.Time))
        {
            long ts = TimeUtil.ToUnix(c.Time);
            if (c.Kind == ChangeKind.Driver && c.Now is not null)
            {
                using var last = Cmd("SELECT now FROM changes WHERE kind = $k AND subject = $s AND ts <= $ts ORDER BY ts DESC LIMIT 1",
                    ("$k", c.Kind.ToString()), ("$s", c.Subject), ("$ts", ts));
                if (last.ExecuteScalar() is string version && version.Equals(c.Now, StringComparison.OrdinalIgnoreCase)) continue;
            }
            using var cmd = Cmd("INSERT OR IGNORE INTO changes(ts, kind, subject, title, was, now, from_ts) VALUES($ts, $k, $s, $t, $w, $n, $f)",
                ("$ts", ts), ("$k", c.Kind.ToString()), ("$s", c.Subject), ("$t", c.Title), ("$w", c.Was), ("$n", c.Now),
                ("$f", c.NoticedFrom is { } from ? TimeUtil.ToUnix(from) : null));
            if (cmd.ExecuteNonQuery() > 0) added.Add(c);
        }
        return added;
    }

    /// <summary>
    /// Sets a reading of the PC (see <see cref="Inventory.Read"/>) against the last one, keeps it as the new last one, and
    /// returns what changed, dated <paramref name="at"/> (not stored yet: see <see cref="InsertChanges"/>). The first
    /// reading of a kind, or of another person's registry, only notes how things are.
    /// </summary>
    public List<SystemChange> ApplyInventory(Dictionary<string, List<InventoryItem>> now, string owner, DateTime at)
    {
        var known = (GetMeta(InventoryKindsKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (GetMeta(InventoryOwnerKey) != owner) known.Clear();
        var before = GetInventory().ToLookup(i => i.Kind);
        var changes = new List<SystemChange>();

        using var tx = BeginTransaction();
        var (goneSince, droppedOut) = HoldMissingCards(now, known, before, at);
        changes.AddRange(droppedOut);
        foreach (var (kind, items) in now)
        {
            if (known.Contains(kind))
                foreach (var change in Inventory.Diff(kind, before[kind], items, at))
                    // A card gone for a second reading was last there before the first: it's dated then.
                    changes.Add(kind == Inventory.Gpu && goneSince.TryGetValue(change.Subject, out long since) && change.Title.StartsWith("Graphics card removed", StringComparison.Ordinal)
                        ? change with { Time = TimeUtil.FromUnix(since) } : change);
            // Nothing to write when it's as it was (the check runs every minute; the disk needn't hear of it).
            if (known.Contains(kind) && Same(before[kind], items)) continue;
            // A list is replaced whole; a fact that couldn't be read this time keeps its last value.
            if (Inventory.IsList(kind) || !known.Contains(kind))
            {
                using var clear = Cmd("DELETE FROM inventory WHERE kind = $k", ("$k", kind));
                clear.ExecuteNonQuery();
            }
            foreach (var item in items)
            {
                using var put = Cmd("INSERT OR REPLACE INTO inventory(kind, key, name, value) VALUES($k, $key, $n, $v)",
                    ("$k", kind), ("$key", item.Key), ("$n", item.Name), ("$v", item.Value));
                put.ExecuteNonQuery();
            }
            known.Add(kind);
        }
        SetMetaIfChanged(InventoryKindsKey, string.Join(',', known.Order()));
        SetMetaIfChanged(InventoryOwnerKey, owner);
        tx.Commit();
        return changes;

        static bool Same(IEnumerable<InventoryItem> stored, List<InventoryItem> read)
        {
            var was = stored.ToDictionary(i => i.Key, i => (i.Name, i.Value));
            return was.Count == read.Count && read.All(i => was.TryGetValue(i.Key, out var w) && w == (i.Name, i.Value));
        }
    }

    private void SetMetaIfChanged(string key, string value)
    {
        if (GetMeta(key) != value) SetMeta(key, value);
    }

    /// <summary>
    /// How long a graphics card must be missing to be taken as removed: as long as readings were apart before they came
    /// every minute, so a driver install's stand-in minutes never pass for a card taken out.
    /// </summary>
    private const long CardGoneAfterSeconds = Inventory.ScanMinutes * 60;

    private const string CardsGoneKey = "inventory_cards_gone";

    /// <summary>
    /// A graphics card isn't taken as removed the first time it's missing from a reading: while its driver is being
    /// installed Windows runs it on a stand-in driver for a minute or two, and a reading that lands then doesn't see it
    /// (on a PC with a second card, that was "Graphics card removed", then "added" ten minutes later). Missing, it's
    /// kept in the list as it was and only noted; still missing ten minutes later, it's gone. Returns the cards now
    /// taken as gone ("gpu:key") and when each was first missed (Unix seconds).
    /// A held card back within the ten minutes with the driver it had is no driver install: it dropped out while the PC
    /// ran (a card losing its slot or its power), and that is said, from when it was first missed. Back with another
    /// driver (or none that could be read), it was the install, which its driver's own line says.
    /// </summary>
    private (Dictionary<string, long> Gone, List<SystemChange> DroppedOut) HoldMissingCards(Dictionary<string, List<InventoryItem>> now, HashSet<string> known,
        ILookup<string, InventoryItem> stored, DateTime at)
    {
        var gone = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var droppedOut = new List<SystemChange>();
        if (!known.Contains(Inventory.Gpu) || !now.TryGetValue(Inventory.Gpu, out var cards)) return (gone, droppedOut);
        var before = stored[Inventory.Gpu];

        var missed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (GetMeta(CardsGoneKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (entry.LastIndexOf('|') is > 0 and var bar && long.TryParse(entry[(bar + 1)..], out long ts)) missed[entry[..bar]] = ts;

        var held = new List<string>();
        foreach (var card in before.ToList())
        {
            if (cards.Any(c => c.Key.Equals(card.Key, StringComparison.OrdinalIgnoreCase)))
            {
                string? driverWas = stored[Inventory.GpuDriver].FirstOrDefault(d => d.Key.Equals(card.Key, StringComparison.OrdinalIgnoreCase))?.Value;
                string? driverNow = now.TryGetValue(Inventory.GpuDriver, out var drivers)
                    ? drivers.FirstOrDefault(d => d.Key.Equals(card.Key, StringComparison.OrdinalIgnoreCase))?.Value : null;
                if (missed.TryGetValue(card.Key, out long outFrom) && driverWas is not null && string.Equals(driverWas, driverNow, StringComparison.OrdinalIgnoreCase))
                {
                    int minutes = Math.Max(1, (int)Math.Round((TimeUtil.ToUnix(at) - outFrom) / 60.0));
                    droppedOut.Add(new SystemChange(TimeUtil.FromUnix(outFrom), ChangeKind.Hardware,
                        $"GPU disconnected for {minutes} minute{(minutes == 1 ? "" : "s")}: {card.Name}") { Subject = $"{Inventory.Gpu}:{card.Key}" });
                }
                continue;
            }
            bool seenMissing = missed.TryGetValue(card.Key, out long since);
            if (seenMissing && TimeUtil.ToUnix(at) - since >= CardGoneAfterSeconds)
            {
                gone[$"{Inventory.Gpu}:{card.Key}"] = since;
                continue;
            }
            cards.Add(card);
            held.Add($"{card.Key}|{(seenMissing ? since : TimeUtil.ToUnix(at))}");
        }
        SetMetaIfChanged(CardsGoneKey, string.Join('\n', held));
        return (gone, droppedOut);
    }

    /// <summary>Until when the event logs were read for drivers and updates (Unix seconds), if ever.</summary>
    public long? ChangesScanned
    {
        get => long.TryParse(GetMeta(ChangesScannedKey), out long ts) ? ts : null;
        set => SetMeta(ChangesScannedKey, (value ?? 0).ToString());
    }

    private void PruneChanges(long before)
    {
        using var c = Cmd("DELETE FROM changes WHERE ts < $t", ("$t", before));
        c.ExecuteNonQuery();
    }

    // ── Reader ────────────────────────────────────────────────────────────

    /// <summary>The changes from <paramref name="from"/> to <paramref name="to"/>, oldest first.</summary>
    public List<SystemChange> GetChanges(long from, long to)
    {
        if (!HasChanges) return [];
        using var cmd = Cmd($"SELECT id, ts, kind, subject, title, was, now, {(HasChangeFrom ? "from_ts" : "NULL")} FROM changes WHERE ts >= $from AND ts < $to ORDER BY ts, id",
            ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        var list = new List<SystemChange>();
        while (r.Read())
        {
            if (!Enum.TryParse<ChangeKind>(r.GetString(2), out var kind)) continue; // written by a newer version
            list.Add(new SystemChange(TimeUtil.FromUnix(r.GetInt64(1)), kind, r.GetString(4))
            {
                Id = r.GetInt64(0), Subject = r.GetString(3), Was = r.IsDBNull(5) ? null : r.GetString(5), Now = r.IsDBNull(6) ? null : r.GetString(6),
                NoticedFrom = r.IsDBNull(7) ? null : TimeUtil.FromUnix(r.GetInt64(7)),
            });
        }
        r.Close();
        // Found within a minute of the check before: that minute is when it happened, with no "~". Changes saved before
        // the rule existed (0.27.1) kept their "~" though the check had already narrowed them to a minute.
        for (int i = 0; i < list.Count; i++)
            if (list[i] is { IsApproximate: true, NoticedFrom: { } found } c && c.Time - found <= Stability.Inventory.WithinAMinute)
                list[i] = c with { Subject = Stability.Inventory.ExactPrefix + c.Subject, NoticedFrom = null };
        JoinFlickers(list);
        // A check that came long after the one before it: the PC was most likely off or asleep in between.
        for (int i = 0; i < list.Count; i++)
            if (list[i] is { IsApproximate: true, NoticedFrom: { } noticed } c && c.Time - noticed > Inventory.WithinAMinute
                && RecordingSince(TimeUtil.ToUnix(noticed), TimeUtil.ToUnix(c.Time)) is long on)
                list[i] = c with { OnFrom = TimeUtil.FromUnix(on) };
        return list;
    }

    /// <summary>How close together a startup program going and coming back is a flicker, not two changes.</summary>
    private static readonly TimeSpan FlickerWithin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A startup program that went and came back within minutes (or came and went) as the one change it was, at the
    /// first's time: "MicrosoftEdgeAutoLaunch left startup and came straight back". Edge deletes its startup entry and
    /// writes it again as you sign in, and the minute's check can land in between; two lines that undo each other read
    /// as a mistake, but it did happen, out of sight.
    /// </summary>
    private static void JoinFlickers(List<SystemChange> list)
    {
        var drop = new HashSet<SystemChange>(ReferenceEqualityComparer.Instance);
        foreach (var item in list.Where(c => c.Kind == ChangeKind.Startup).GroupBy(c => Item(c.Subject), StringComparer.OrdinalIgnoreCase))
        {
            var changes = item.OrderBy(c => c.Time).ToList();
            for (int i = 0; i + 1 < changes.Count; i++)
                if (changes[i + 1].Time - changes[i].Time <= FlickerWithin)
                {
                    // Two flips of a startup program put it back as it was.
                    var (first, back) = (changes[i], changes[i + 1]);
                    list[list.IndexOf(first)] = first with { Subject = item.Key, Title = FlickerTitle(first.Title) };
                    drop.Add(back);
                    i++;
                }
        }
        if (drop.Count > 0) list.RemoveAll(drop.Contains);

        static string Item(string subject) => subject.StartsWith(Inventory.ExactPrefix, StringComparison.Ordinal) ? subject[Inventory.ExactPrefix.Length..] : subject;
    }

    /// <summary>The joined line, from the first change's title (see <see cref="Inventory.Diff"/>).</summary>
    internal static string FlickerTitle(string first)
    {
        foreach (var (ends, said) in new[]
        {
            (" no longer starts with Windows", "left startup and came straight back"),
            (" now starts with Windows", "was added to startup and taken off straight away"),
            (" turned off at startup", "was turned off at startup and straight back on"),
            (" turned on at startup", "was turned on at startup and straight back off"),
        })
            if (first.EndsWith(ends, StringComparison.Ordinal)) return $"{first[..^ends.Length]} {said}";
        return first;
    }

    /// <summary>
    /// The first minute of the unbroken run of recorded minutes that reaches <paramref name="to"/>, when there is a
    /// stretch with nothing recorded between it and <paramref name="from"/> (null: recorded throughout, or nothing
    /// recorded near <paramref name="to"/>).
    /// </summary>
    private long? RecordingSince(long from, long to)
    {
        const int Break = 150; // as on the graphs: minutes more than two and a half apart have a gap between them
        using var cmd = Cmd("SELECT ts FROM system_minute WHERE ts > $from AND ts <= $to ORDER BY ts DESC", ("$from", from - 60), ("$to", to));
        using var r = cmd.ExecuteReader();
        long start = to;
        bool any = false;
        while (r.Read())
        {
            long ts = r.GetInt64(0);
            if (start - ts > Break) return any ? start : null;
            start = ts;
            any = true;
        }
        return any && start - from > Break ? start : null;
    }

    /// <summary>The PC as last read: its apps, startup programs, hardware and settings.</summary>
    /// <summary>The oldest change on record (Unix seconds), or null.</summary>
    public long? FirstChangeTime()
    {
        if (!HasChanges) return null;
        using var cmd = Cmd("SELECT min(ts) FROM changes");
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    /// <summary>One thing the inventory holds a single value for ("ram": "16 GB"), or null.</summary>
    public string? GetInventoryValue(string kind)
    {
        if (!HasChanges) return null;
        using var cmd = Cmd("SELECT value FROM inventory WHERE kind = $kind LIMIT 1", ("$kind", kind));
        return cmd.ExecuteScalar() as string;
    }

    public List<InventoryItem> GetInventory()
    {
        if (!HasChanges) return [];
        using var cmd = Cmd("SELECT kind, key, name, value FROM inventory ORDER BY kind, name");
        using var r = cmd.ExecuteReader();
        var list = new List<InventoryItem>();
        while (r.Read()) list.Add(new InventoryItem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }
}
