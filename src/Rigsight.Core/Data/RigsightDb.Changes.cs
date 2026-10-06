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
        var goneSince = HoldMissingCards(now, known, before[Inventory.Gpu], at);
        foreach (var (kind, items) in now)
        {
            if (known.Contains(kind))
                foreach (var change in Inventory.Diff(kind, before[kind], items, at))
                    // A card gone for a second reading was last there before the first: it's dated then.
                    changes.Add(kind == Inventory.Gpu && goneSince.TryGetValue(change.Subject, out long since) && change.Title.StartsWith("Graphics card removed", StringComparison.Ordinal)
                        ? change with { Time = TimeUtil.FromUnix(since) } : change);
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
        SetMeta(InventoryKindsKey, string.Join(',', known.Order()));
        SetMeta(InventoryOwnerKey, owner);
        tx.Commit();
        return changes;
    }

    private const string CardsGoneKey = "inventory_cards_gone";

    /// <summary>
    /// A graphics card isn't taken as removed the first time it's missing from a reading: while its driver is being
    /// installed Windows runs it on a stand-in driver for a minute or two, and a reading that lands then doesn't see it
    /// (on a PC with a second card, that was "Graphics card removed", then "added" ten minutes later). Missing once, it's
    /// kept in the list as it was and only noted; missing again at the next reading, it's gone. Returns the cards now
    /// taken as gone ("gpu:key") and when each was first missed (Unix seconds).
    /// </summary>
    private Dictionary<string, long> HoldMissingCards(Dictionary<string, List<InventoryItem>> now, HashSet<string> known, IEnumerable<InventoryItem> before, DateTime at)
    {
        var gone = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!known.Contains(Inventory.Gpu) || !now.TryGetValue(Inventory.Gpu, out var cards)) return gone;

        var missed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (GetMeta(CardsGoneKey) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (entry.LastIndexOf('|') is > 0 and var bar && long.TryParse(entry[(bar + 1)..], out long ts)) missed[entry[..bar]] = ts;

        var held = new List<string>();
        foreach (var card in before.ToList())
        {
            if (cards.Any(c => c.Key.Equals(card.Key, StringComparison.OrdinalIgnoreCase))) continue;
            if (missed.TryGetValue(card.Key, out long since))
            {
                gone[$"{Inventory.Gpu}:{card.Key}"] = since;
                continue;
            }
            cards.Add(card);
            held.Add($"{card.Key}|{TimeUtil.ToUnix(at)}");
        }
        SetMeta(CardsGoneKey, string.Join('\n', held));
        return gone;
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
        return list;
    }

    /// <summary>The PC as last read: its apps, startup programs, hardware and settings.</summary>
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
