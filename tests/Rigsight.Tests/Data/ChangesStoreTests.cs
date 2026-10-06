using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Data;

/// <summary>The stored changes and the PC's last inventory: what's kept once, what a new reading adds, what clean-ups leave.</summary>
public sealed class ChangesStoreTests
{
    private static readonly DateTime T = new(2026, 9, 28, 19, 15, 0);

    private static SystemChange Driver(DateTime at, string inf, string version) =>
        new(at, ChangeKind.Driver, $"Realtek audio driver {version}") { Subject = inf, Now = version };

    private static Dictionary<string, List<InventoryItem>> Pc(params (string Name, string Version)[] apps) => new()
    {
        [Inventory.App] = Inventory.AppItems(apps),
        [Inventory.Bios] = [new(Inventory.Bios, Inventory.Bios, "BIOS", "F66d")],
    };

    [Fact]
    public void Changes_are_stored_once_and_read_back_whole_in_order()
    {
        using var t = new TestDb();
        var update = new SystemChange(T.AddHours(-2), ChangeKind.WindowsUpdate, "Windows update KB5065789") { Subject = "2026-09 Cumulative Update (KB5065789)" };
        var app = new SystemChange(T, ChangeKind.AppUpdated, "7-Zip updated to 26.01") { Subject = "app:7-zip", Was = "25.01", Now = "26.01" };
        Assert.Equal(2, t.Db.InsertChanges([app, update]).Count);
        Assert.Empty(t.Db.InsertChanges([app, update])); // read from the logs again
        Assert.Equal(2, t.Count("changes"));

        using var reader = t.Reader();
        var read = reader.GetChanges(0, long.MaxValue / 2);
        Assert.Equal(["Windows update KB5065789", "7-Zip updated to 26.01"], read.Select(c => c.Title));
        Assert.Equal((T, ChangeKind.AppUpdated, "app:7-zip", "25.01", "26.01"), (read[1].Time, read[1].Kind, read[1].Subject, read[1].Was, read[1].Now));
        Assert.Null(read[0].Was);
        Assert.True(read.All(c => c.Id > 0));
        Assert.Equal(["7-Zip updated to 26.01"], reader.GetChanges(TimeUtil.ToUnix(T), TimeUtil.ToUnix(T) + 1).Select(c => c.Title));
        Assert.Empty(reader.GetChanges(TimeUtil.ToUnix(T) + 1, long.MaxValue / 2));
    }

    [Fact]
    public void When_the_check_before_ran_is_kept_with_a_change_found_by_a_check()
    {
        using var t = new TestDb();
        var found = new SystemChange(T, ChangeKind.AppUpdated, "Steam updated to 2.11") { Subject = "app:steam", NoticedFrom = T.AddMinutes(-7) };
        var old = new SystemChange(T.AddHours(1), ChangeKind.AppUpdated, "Edge updated to 2") { Subject = "app:edge" }; // recorded before this was kept
        var logged = new SystemChange(T.AddHours(2), ChangeKind.WindowsUpdate, "Windows update KB1") { Subject = "KB1" };
        t.Db.InsertChanges([found, old, logged]);
        var read = t.Db.GetChanges(0, long.MaxValue / 2);
        Assert.Equal([T.AddMinutes(-7), null, null], read.Select(c => c.NoticedFrom));
        Assert.Equal([T.AddMinutes(-7), T.AddHours(1).AddMinutes(-Inventory.ScanMinutes), T.AddHours(2)], read.Select(c => c.Earliest));

        // A database from 0.15.0 has no such column: the agent adds it, and the app reads either.
        t.Db.Dispose();
        t.Exec("ALTER TABLE changes DROP COLUMN from_ts");
        using (var reader = t.Reader()) Assert.All(reader.GetChanges(0, long.MaxValue / 2), c => Assert.Null(c.NoticedFrom));
        var db = t.OpenWriter();
        Assert.Equal(3, db.GetChanges(0, long.MaxValue / 2).Count);
        db.InsertChanges([found with { Time = T.AddDays(1), NoticedFrom = T.AddDays(1).AddMinutes(-10) }]);
        Assert.Equal(T.AddDays(1).AddMinutes(-10), db.GetChanges(0, long.MaxValue / 2)[^1].NoticedFrom);
    }

    [Fact]
    public void A_driver_counts_once_per_version()
    {
        using var t = new TestDb();
        // A VPN adapter created again and again "installs" the same driver each time.
        Assert.Single(t.Db.InsertChanges([Driver(T, "oem12", "6.0.9")]));
        Assert.Empty(t.Db.InsertChanges([Driver(T.AddDays(1), "oem12", "6.0.9")]));
        Assert.Empty(t.Db.InsertChanges([Driver(T.AddDays(2), "oem12", "6.0.9"), Driver(T.AddDays(3), "oem12", "6.0.9")]));
        // A new version counts, and so does going back to the old one; another driver is another driver.
        Assert.Single(t.Db.InsertChanges([Driver(T.AddDays(4), "oem12", "6.1.0")]));
        Assert.Single(t.Db.InsertChanges([Driver(T.AddDays(5), "oem12", "6.0.9")]));
        Assert.Single(t.Db.InsertChanges([Driver(T.AddDays(5), "oem40", "6.0.9")]));
        Assert.Equal(4, t.Count("changes"));
    }

    [Fact]
    public void An_older_log_entry_read_late_doesnt_hide_behind_a_newer_one()
    {
        using var t = new TestDb();
        t.Db.InsertChanges([Driver(T.AddDays(4), "oem12", "6.1.0")]);
        Assert.Single(t.Db.InsertChanges([Driver(T, "oem12", "6.0.9")])); // from before the one already stored
        Assert.Empty(t.Db.InsertChanges([Driver(T, "oem12", "6.0.9")]));
    }

    [Fact]
    public void A_driver_whose_version_isnt_known_counts_each_time_it_is_installed()
    {
        using var t = new TestDb();
        SystemChange Nvidia(DateTime at) => new(at, ChangeKind.Driver, "NVIDIA graphics driver installed") { Subject = "nvidia-graphics" };
        Assert.Equal(2, t.Db.InsertChanges([Nvidia(T), Nvidia(T.AddDays(30))]).Count);
        Assert.Empty(t.Db.InsertChanges([Nvidia(T)]));
    }

    [Fact]
    public void The_first_inventory_only_notes_how_things_are()
    {
        using var t = new TestDb();
        Assert.Empty(t.Db.ApplyInventory(Pc(("Steam", "2.10"), ("Discord", "1.0")), "me", T));
        Assert.Equal(3, t.Count("inventory"));
        Assert.Equal(0, t.Count("changes"));
        Assert.Equal(["BIOS", "Discord", "Steam"], t.Db.GetInventory().Select(i => i.Name).Order());
    }

    [Fact]
    public void A_later_inventory_gives_the_changes_and_becomes_the_last_one()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10"), ("Discord", "1.0")), "me", T);
        var now = Pc(("Steam", "2.11"), ("OBS Studio", "32.1.2"));
        now[Inventory.Bios] = [new(Inventory.Bios, Inventory.Bios, "BIOS", "F67")];
        var changes = t.Db.ApplyInventory(now, "me", T.AddHours(1));
        Assert.Equal(["BIOS updated to F67", "Discord removed", "OBS Studio installed", "Steam updated to 2.11"], changes.Select(c => c.Title).Order());
        Assert.All(changes, c => Assert.Equal(T.AddHours(1), c.Time));
        Assert.Equal(0, t.Count("changes")); // the caller stores them (it may know better when)
        Assert.Empty(t.Db.ApplyInventory(now, "me", T.AddHours(2)));
        Assert.Equal(["BIOS", "OBS Studio", "Steam"], t.Db.GetInventory().Select(i => i.Name).Order());
    }

    [Fact]
    public void A_kind_that_couldnt_be_read_keeps_what_was_known()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10")), "me", T);
        // The apps couldn't be read this time: nothing is taken as removed, and the next reading compares with the last good one.
        Assert.Empty(t.Db.ApplyInventory(new() { [Inventory.Bios] = [new(Inventory.Bios, Inventory.Bios, "BIOS", "F66d")] }, "me", T.AddHours(1)));
        Assert.Equal("Steam updated to 2.11", Assert.Single(t.Db.ApplyInventory(Pc(("Steam", "2.11")), "me", T.AddHours(2))).Title);
        // A fact that came back empty keeps its value too.
        Assert.Empty(t.Db.ApplyInventory(new() { [Inventory.Bios] = [] }, "me", T.AddHours(3)));
        Assert.Contains(t.Db.GetInventory(), i => i is { Kind: Inventory.Bios, Value: "F66d" });
    }

    [Fact]
    public void A_kind_read_for_the_first_time_later_is_noted_without_changes()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10")), "me", T);
        var now = Pc(("Steam", "2.10"));
        now[Inventory.Startup] = Inventory.StartupItems([("Steam", true), ("Discord", true)]);
        Assert.Empty(t.Db.ApplyInventory(now, "me", T.AddHours(1)));
        now[Inventory.Startup] = Inventory.StartupItems([("Steam", true)]);
        Assert.Equal("Discord no longer starts with Windows", Assert.Single(t.Db.ApplyInventory(now, "me", T.AddHours(2))).Title);
    }

    [Fact]
    public void Another_persons_registry_isnt_a_change_on_the_PC()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10"), ("Discord", "1.0")), "S-1-5-21-user", T);
        // The agent restarted as an administrator: their apps differ. Noted, not listed as installs and removals.
        Assert.Empty(t.Db.ApplyInventory(Pc(("Steam", "2.10"), ("Visual Studio", "18.0")), "S-1-5-21-admin", T.AddHours(1)));
        Assert.Equal(["BIOS", "Steam", "Visual Studio"], t.Db.GetInventory().Select(i => i.Name).Order());
        Assert.Equal("Steam removed", Assert.Single(t.Db.ApplyInventory(Pc(("Visual Studio", "18.0")), "S-1-5-21-admin", T.AddHours(2))).Title);
    }

    private static Dictionary<string, List<InventoryItem>> Cards(params (string Device, string Name, string Driver)[] cards)
    {
        var (items, drivers) = Inventory.GraphicsItems(cards.Select(c => (c.Device, c.Name, "NVIDIA", c.Driver, (string?)null)));
        return new() { [Inventory.Gpu] = items, [Inventory.GpuDriver] = drivers };
    }

    private static readonly (string, string, string) Intel = (@"pci\ven_8086&dev_a780", "Intel UHD Graphics 770", "32.0.101.5972");

    [Fact]
    public void A_card_missing_from_one_reading_while_its_driver_installs_is_not_a_card_removed()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Cards(Intel, (@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "32.0.16.1047")), "me", T);
        // The reading lands mid-install: only the other card is there (Windows runs this one on its stand-in driver).
        Assert.Empty(t.Db.ApplyInventory(Cards(Intel), "me", T.AddMinutes(10)));
        Assert.Equal(2, t.Db.GetInventory().Count(i => i.Kind == Inventory.Gpu));
        // Ten minutes on it's back with its new driver: one change, the driver.
        var change = Assert.Single(t.Db.ApplyInventory(Cards(Intel, (@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "32.0.16.1692")), "me", T.AddMinutes(20)));
        Assert.Equal(ChangeKind.Driver, change.Kind);
        Assert.Contains("616.92", change.Title);
        // And a later reading without it starts over: missed once, not gone.
        Assert.Empty(t.Db.ApplyInventory(Cards(Intel), "me", T.AddMinutes(30)));
    }

    [Fact]
    public void A_card_missing_from_two_readings_running_was_removed_when_it_was_first_missed()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Cards(Intel, (@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "32.0.16.1047")), "me", T);
        // Swapped for another: the new one is there at once, the old one's going is held for a reading.
        (string, string, string) radeon = (@"pci\ven_1002&dev_7550", "Radeon RX 9070 XT", "32.0.21025.1024");
        Assert.Equal("Graphics card added: Radeon RX 9070 XT", Assert.Single(t.Db.ApplyInventory(Cards(Intel, radeon), "me", T.AddMinutes(10))).Title);
        var removed = Assert.Single(t.Db.ApplyInventory(Cards(Intel, radeon), "me", T.AddMinutes(20)));
        Assert.Equal("Graphics card removed: RTX 3080 Ti", removed.Title);
        Assert.Equal(T.AddMinutes(10), removed.Time); // when it was first missing, beside the card that replaced it
        Assert.Empty(t.Db.ApplyInventory(Cards(Intel, radeon), "me", T.AddMinutes(30)));
        Assert.Equal(2, t.Db.GetInventory().Count(i => i.Kind == Inventory.Gpu));
    }

    [Fact]
    public void The_inventory_survives_the_agent_restarting()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10")), "me", T);
        t.Db.ChangesScanned = TimeUtil.ToUnix(T);
        var db = t.OpenWriter();
        Assert.Equal(TimeUtil.ToUnix(T), db.ChangesScanned);
        Assert.Equal("Steam updated to 2.11", Assert.Single(db.ApplyInventory(Pc(("Steam", "2.11")), "me", T.AddDays(1))).Title);
    }

    [Fact]
    public void Nothing_is_scanned_until_it_is()
    {
        using var t = new TestDb();
        Assert.Null(t.Db.ChangesScanned);
    }

    [Fact]
    public void Old_changes_go_with_the_rest_of_the_history_and_clearing_keeps_the_inventory()
    {
        using var t = new TestDb();
        t.Db.ApplyInventory(Pc(("Steam", "2.10")), "me", T);
        t.Db.InsertChanges([Driver(T.AddDays(-10), "a", "1"), Driver(T, "b", "1")]);
        t.Db.Prune(TimeUtil.ToUnix(T));
        Assert.Equal(["b"], t.Rows("SELECT subject FROM changes").Select(r => (string)r["subject"]!));
        Assert.Equal(2, t.Count("inventory"));

        t.Db.ClearHistory();
        Assert.Equal(0, t.Count("changes"));
        // Still known: clearing the history mustn't list every installed app as new the next minute.
        Assert.Empty(t.Db.ApplyInventory(Pc(("Steam", "2.10")), "me", T.AddHours(1)));
    }

    [Fact]
    public void A_database_from_before_reads_as_no_changes()
    {
        using var t = new TestDb();
        t.Db.Dispose();
        t.Exec("DROP TABLE changes; DROP TABLE inventory;");
        using var reader = t.Reader();
        Assert.False(reader.HasChanges);
        Assert.Empty(reader.GetChanges(0, long.MaxValue / 2));
        Assert.Empty(reader.GetInventory());
        Assert.Null(reader.ChangesScanned);
    }

    [Fact]
    public void A_kind_from_a_newer_version_is_skipped_not_misread()
    {
        using var t = new TestDb();
        t.Db.InsertChanges([Driver(T, "a", "1")]);
        t.Exec("INSERT INTO changes(ts, kind, subject, title) VALUES(1, 'SomethingNew', 's', 'From the future')");
        Assert.Equal("Realtek audio driver 1", Assert.Single(t.Db.GetChanges(0, long.MaxValue / 2)).Title);
    }
}
