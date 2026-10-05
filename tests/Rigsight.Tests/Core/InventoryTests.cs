using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// What the PC's inventory makes of the registry (names as real installers write them), and what two readings set side
/// by side call a change. The machine's own registry is only read in the smoke test at the end.
/// </summary>
public sealed class InventoryTests
{
    private static readonly DateTime At = new(2026, 9, 28, 19, 15, 0);

    // ---- App names and versions ----

    [Theory]
    [InlineData("7-Zip 26.01 (x64)", "26.01", "7-Zip (x64)", "26.01")]
    [InlineData("CPUID CPU-Z 2.15", "2.15", "CPUID CPU-Z", "2.15")]
    [InlineData("Inno Setup version 6.7.3", "6.7.3", "Inno Setup", "6.7.3")]
    [InlineData("Windhawk v1.7.3", "1.7.3", "Windhawk", "1.7.3")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "14.51.36247.0", "Microsoft Visual C++ v14 Redistributable (x64)", "14.51.36247.0")]
    [InlineData("Microsoft Visual C++ 2010  x64 Redistributable - 10.0.40219", "10.0.40219", "Microsoft Visual C++ 2010 x64 Redistributable", "10.0.40219")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.27 (x64)", "8.0.27.36030", "Microsoft Windows Desktop Runtime (x64)", "8.0.27.36030")]
    [InlineData("Microsoft .NET SDK 10.0.401 (x64)", "10.4.126.42413", "Microsoft .NET SDK (x64)", "10.4.126.42413")]
    [InlineData("Python 3.13.2 (64-bit)", "3.13.2150.0", "Python (64-bit)", "3.13.2150.0")]
    [InlineData("NVIDIA FrameView SDK 1.9.12728.38614306", "1.9.12728.38614306", "NVIDIA FrameView SDK", "1.9.12728.38614306")]
    [InlineData("WinRAR 7.10 (64-bit)", "7.10.0", "WinRAR (64-bit)", "7.10.0")]
    [InlineData("Google Chrome", "154.0.8037.95", "Google Chrome", "154.0.8037.95")]
    [InlineData("Cheat Engine 7.6", "", "Cheat Engine", "7.6")] // no version listed: the one in the name
    [InlineData("Counter-Strike 2", "", "Counter-Strike 2", "")] // a number isn't a version
    [InlineData("Dota 2", "", "Dota 2", "")]
    [InlineData("Postman x86_64 11.55.0", "11.55.0", "Postman x86_64", "11.55.0")]
    [InlineData("Riot Client ", "", "Riot Client", "")]
    public void An_apps_name_is_split_from_its_version(string display, string listed, string name, string version) =>
        Assert.Equal((name, version), Inventory.SplitName(display, listed));

    [Fact]
    public void An_update_keeps_the_apps_key_so_it_reads_as_one_app()
    {
        var before = Inventory.AppItems([("7-Zip 25.01 (x64)", "25.01")]);
        var after = Inventory.AppItems([("7-Zip 26.01 (x64)", "26.01")]);
        Assert.Equal(before.Single().Key, after.Single().Key);
        var change = Assert.Single(Inventory.Diff(Inventory.App, before, after, At));
        Assert.Equal((ChangeKind.AppUpdated, "7-Zip (x64) updated to 26.01", "25.01", "26.01"), (change.Kind, change.Title, change.Was, change.Now));
        Assert.Equal("Was 25.01", change.Detail);
        Assert.Equal(At, change.Time);
    }

    [Fact]
    public void The_same_app_listed_twice_is_one_and_two_versions_side_by_side_are_one_app_with_both()
    {
        var items = Inventory.AppItems([("Steam", "2.10"), ("Steam", "2.10"), ("Python 3.12.1 (64-bit)", "3.12.1"), ("Python 3.13.2 (64-bit)", "3.13.2")]);
        Assert.Equal(["Python (64-bit)", "Steam"], items.Select(i => i.Name).Order());
        Assert.Equal("3.12.1, 3.13.2", items.Single(i => i.Name.StartsWith("Python")).Value);
    }

    [Theory]
    [InlineData("NVIDIA Graphics Driver 616.92")] // followed as a driver instead
    [InlineData("Intel(R) Graphics Driver")]
    [InlineData("Rigsight")]
    [InlineData("")]
    public void Some_entries_arent_apps(string display) => Assert.Empty(Inventory.AppItems([(display, "1.0")]));

    [Fact]
    public void Apps_installed_and_removed_say_so()
    {
        var before = Inventory.AppItems([("Stremio", "5.0.26"), ("Steam", "2.10")]);
        var after = Inventory.AppItems([("Steam", "2.10"), ("OBS Studio", "32.1.2"), ("No Man's Sky", "")]);
        var changes = Inventory.Diff(Inventory.App, before, after, At);
        Assert.Equal(["No Man's Sky installed", "OBS Studio installed", "Stremio removed"], changes.Select(c => c.Title).Order());
        Assert.Equal("32.1.2", changes.Single(c => c.Title.StartsWith("OBS")).Now);
        Assert.Null(changes.Single(c => c.Title.StartsWith("No Man")).Now);
        Assert.Equal((ChangeKind.AppRemoved, "5.0.26"), (changes.Single(c => c.Title.StartsWith("Stremio")).Kind, changes.Single(c => c.Title.StartsWith("Stremio")).Was));
    }

    [Fact]
    public void Nothing_changed_is_no_changes()
    {
        var apps = Inventory.AppItems([("Steam", "2.10"), ("Discord", "1.0.9260")]);
        Assert.Empty(Inventory.Diff(Inventory.App, apps, Inventory.AppItems([("Discord", "1.0.9260"), ("steam", "2.10")]), At));
    }

    // ---- Startup programs ----

    [Theory]
    [InlineData("CometUpdaterTaskUser145.2.7632.4583", "CometUpdaterTaskUser")]
    [InlineData("GoogleChromeAutoLaunch_7E36FFB55DA820F16E11CF1EFF742541", "GoogleChromeAutoLaunch")]
    [InlineData("NVIDIA Broadcast ", "NVIDIA Broadcast")]
    [InlineData("Steam", "Steam")]
    [InlineData("1.2.3", "1.2.3")] // nothing left: as written
    public void Startup_names_lose_their_version_and_code(string raw, string name) =>
        Assert.Equal(name, Inventory.StartupItems([(raw, true)]).Single().Name);

    [Fact]
    public void Startup_programs_come_go_and_are_switched()
    {
        var before = Inventory.StartupItems([("Steam", true), ("Discord", true), ("OneDrive", false), ("LGHUB", true), ("Docker Desktop", false)]);
        var after = Inventory.StartupItems([("Steam", false), ("OneDrive", true), ("LGHUB", true), ("FACEIT", true), ("Epic", false)]);
        var titles = Inventory.Diff(Inventory.Startup, before, after, At).Select(c => c.Title).Order().ToList();
        // Docker (off, then gone) and Epic (added but off) never started with Windows: not worth a line.
        Assert.Equal(["Discord no longer starts with Windows", "FACEIT now starts with Windows", "OneDrive turned on at startup", "Steam turned off at startup"], titles);
    }

    [Fact]
    public void An_updaters_new_version_isnt_a_new_startup_program()
    {
        var before = Inventory.StartupItems([("CometUpdaterTaskUser145.2.7632.4583", true)]);
        var after = Inventory.StartupItems([("CometUpdaterTaskUser146.0.7700.1", true)]);
        Assert.Empty(Inventory.Diff(Inventory.Startup, before, after, At));
    }

    [Fact]
    public void A_program_listed_for_the_user_and_the_PC_starts_if_either_is_on()
    {
        Assert.Equal(Inventory.On, Inventory.StartupItems([("Steam", false), ("steam", true)]).Single().Value);
        Assert.Equal(Inventory.On, Inventory.StartupItems([("Steam", true), ("steam", false)]).Single().Value);
    }

    // ---- Graphics ----

    [Theory]
    [InlineData("NVIDIA", "32.0.16.1692", null, "616.92")]
    [InlineData("NVIDIA", "32.0.15.8097", null, "580.97")]
    [InlineData("NVIDIA", "31.0.15.3623", null, "536.23")]
    [InlineData("AMD", "32.0.21025.1024", "25.9.1", "25.9.1")]
    [InlineData("AMD", "32.0.21025.1024", null, "32.0.21025.1024")]
    [InlineData("Intel", "32.0.101.6078", null, "32.0.101.6078")]
    [InlineData("NVIDIA", "1.2", null, "1.2")] // too short to hold NVIDIA's version
    public void A_graphics_driver_shows_its_makers_version(string maker, string version, string? radeon, string expected) =>
        Assert.Equal(expected, Inventory.DriverVersion(maker, version, radeon));

    [Fact]
    public void Only_real_cards_count_and_each_has_its_driver()
    {
        var (cards, drivers) = Inventory.GraphicsItems(
        [
            (@"pci\ven_10de&dev_2208", "NVIDIA GeForce RTX 3080 Ti", "NVIDIA", "32.0.16.1692", null),
            ("usbmmidd", "USB Mobile Monitor Virtual Display", "Amyuni", "2.0.0.1", null),
            (@"PCI\CC_0300", "Microsoft Basic Display Adapter", "Microsoft", "10.0.26100.1", null),
        ]);
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", Assert.Single(cards).Name);
        var driver = Assert.Single(drivers);
        Assert.Equal(("NVIDIA graphics driver", "616.92", cards[0].Key), (driver.Name, driver.Value, driver.Key));
    }

    [Fact]
    public void Two_of_the_same_card_are_two_cards()
    {
        var (cards, _) = Inventory.GraphicsItems([(@"pci\ven_10de&dev_2208", "RTX", "NVIDIA", "32.0.16.1692", null), (@"pci\ven_10de&dev_2208", "RTX", "NVIDIA", "32.0.16.1692", null)]);
        Assert.Equal(2, cards.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public void A_new_driver_version_is_a_driver_change_with_the_old_one()
    {
        var (_, before) = Inventory.GraphicsItems([(@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "NVIDIA", "32.0.16.1047", null)]);
        var (_, after) = Inventory.GraphicsItems([(@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "NVIDIA", "32.0.16.1692", null)]);
        var change = Assert.Single(Inventory.Diff(Inventory.GpuDriver, before, after, At));
        Assert.Equal((ChangeKind.Driver, "NVIDIA graphics driver 616.92", "610.47"), (change.Kind, change.Title, change.Was));
        Assert.True(change.IsGraphicsDriver);
        Assert.True(change.IsSystemLevel);
        Assert.Equal("Installed: NVIDIA graphics driver 616.92", change.Line);
    }

    [Fact]
    public void A_swapped_card_is_one_removed_and_one_added_and_its_driver_isnt_an_update()
    {
        var (oldCards, oldDrivers) = Inventory.GraphicsItems([(@"pci\ven_10de&dev_2208", "RTX 3080 Ti", "NVIDIA", "32.0.16.1692", null)]);
        var (newCards, newDrivers) = Inventory.GraphicsItems([(@"pci\ven_1002&dev_7550", "Radeon RX 9070 XT", "Advanced Micro Devices, Inc.", "32.0.21025.1024", "25.9.1")]);
        Assert.Equal(["Graphics card added: Radeon RX 9070 XT", "Graphics card removed: RTX 3080 Ti"],
            Inventory.Diff(Inventory.Gpu, oldCards, newCards, At).Select(c => c.Title).Order());
        Assert.Empty(Inventory.Diff(Inventory.GpuDriver, oldDrivers, newDrivers, At));
        Assert.Equal("AMD graphics driver", newDrivers.Single().Name);
    }

    // ---- Drives ----

    [Fact]
    public void Drives_inside_the_PC_count_and_USB_and_virtual_ones_dont()
    {
        var disks = Inventory.DiskItems(
        [
            (@"SCSI\Disk&Ven_NVMe&Prod_Samsung_SSD_980\5&38ab24df&0&000000", "Samsung SSD 980 1TB"),
            (@"SCSI\Disk&Ven_&Prod_ST2000VX008-2E31\5&2b637674&0&000000", "ST2000VX008-2E3164"),
            (@"USBSTOR\Disk&Ven_SanDisk&Prod_Ultra\4C530001", "SanDisk Ultra USB Device"),
            (@"SCSI\Disk&Ven_Msft&Prod_Virtual_Disk\2&1f4adffe&0&000001", "Msft Virtual Disk"),
            (@"SCSI\Disk&Ven_X\1", ""),
        ]);
        Assert.Equal(["Samsung SSD 980 1TB", "ST2000VX008-2E3164"], disks.Select(d => d.Name));
    }

    [Fact]
    public void A_drive_moved_to_another_port_is_the_same_drive()
    {
        var before = Inventory.DiskItems([(@"SCSI\Disk&Ven_&Prod_KINGSTON\5&2b637674&0&050000", "KINGSTON SA400S37480G")]);
        var after = Inventory.DiskItems([(@"SCSI\Disk&Ven_&Prod_KINGSTON\5&2b637674&0&020000", "KINGSTON SA400S37480G")]);
        Assert.Empty(Inventory.Diff(Inventory.Disk, before, after, At));
    }

    [Fact]
    public void A_drive_added_or_gone_is_hardware_and_two_alike_are_told_apart()
    {
        var one = Inventory.DiskItems([("a", "Samsung SSD 990 PRO 2TB")]);
        var two = Inventory.DiskItems([("a", "Samsung SSD 990 PRO 2TB"), ("b", "Samsung SSD 990 PRO 2TB")]);
        var added = Assert.Single(Inventory.Diff(Inventory.Disk, one, two, At));
        Assert.Equal((ChangeKind.Hardware, "Drive added: Samsung SSD 990 PRO 2TB"), (added.Kind, added.Title));
        Assert.Equal("Drive removed: Samsung SSD 990 PRO 2TB", Assert.Single(Inventory.Diff(Inventory.Disk, two, one, At)).Title);
    }

    // ---- Facts: a value that changes ----

    [Theory]
    [InlineData(Inventory.Cpu, "Processor", "Ryzen 7 5700X3D", "Ryzen 7 9800X3D", ChangeKind.Hardware, "Processor changed to Ryzen 7 9800X3D")]
    [InlineData(Inventory.Ram, "Memory", "16 GB", "32 GB", ChangeKind.Hardware, "Memory changed to 32 GB")]
    [InlineData(Inventory.Board, "Motherboard", "B450 AORUS PRO-CF", "B650 AORUS ELITE", ChangeKind.Hardware, "Motherboard changed to B650 AORUS ELITE")]
    [InlineData(Inventory.Bios, "BIOS", "F66d", "F67", ChangeKind.Firmware, "BIOS updated to F67")]
    [InlineData(Inventory.Windows, "Windows 11", "24H2", "25H2", ChangeKind.Windows, "Windows 11 updated to 25H2")]
    public void A_fact_that_changes_says_what_to_and_what_it_was(string kind, string name, string was, string now, ChangeKind expected, string title)
    {
        var change = Assert.Single(Inventory.Diff(kind, [new(kind, kind, name, was)], [new(kind, kind, name, now)], At));
        Assert.Equal((expected, title, was, now, $"Was {was}"), (change.Kind, change.Title, change.Was, change.Now, change.Detail));
        Assert.True(change.IsSystemLevel);
    }

    [Theory]
    [InlineData(Inventory.Off, Inventory.On, "Fast Startup turned on")]
    [InlineData(Inventory.On, Inventory.Off, "Fast Startup turned off")]
    public void A_setting_switched_says_which_way(string was, string now, string title)
    {
        var change = Assert.Single(Inventory.Diff(Inventory.Setting, [new(Inventory.Setting, "fast-startup", "Fast Startup", was)], [new(Inventory.Setting, "fast-startup", "Fast Startup", now)], At));
        Assert.Equal((ChangeKind.Setting, title), (change.Kind, change.Title));
        Assert.Null(change.Detail);
        Assert.False(change.IsSystemLevel);
    }

    [Theory]
    [InlineData(Inventory.Cpu)]
    [InlineData(Inventory.Ram)]
    [InlineData(Inventory.Bios)]
    [InlineData(Inventory.Windows)]
    [InlineData(Inventory.Setting)]
    [InlineData(Inventory.GpuDriver)]
    public void A_fact_seen_for_the_first_time_or_not_read_this_time_isnt_a_change(string kind)
    {
        InventoryItem[] one = [new(kind, "k", "Name", "value")];
        Assert.Empty(Inventory.Diff(kind, [], one, At));
        Assert.Empty(Inventory.Diff(kind, one, [], At));
    }

    [Fact]
    public void Lists_and_facts_are_told_apart()
    {
        Assert.All(new[] { Inventory.App, Inventory.Startup, Inventory.Gpu, Inventory.Disk }, k => Assert.True(Inventory.IsList(k)));
        Assert.All(new[] { Inventory.Cpu, Inventory.Ram, Inventory.Board, Inventory.Bios, Inventory.Windows, Inventory.Setting, Inventory.GpuDriver }, k => Assert.False(Inventory.IsList(k)));
    }

    // ---- Drive space ----

    private static DriveDay Space(int day, double used, double total = 931, string drive = "D:\\") =>
        new() { Day = TimeUtil.ToUnix(new DateTime(2026, 9, day)), Drive = drive, UsedGb = used, TotalGb = total };

    [Fact]
    public void A_jump_or_drop_in_a_drives_space_is_a_change_on_that_day()
    {
        var changes = StorageChanges.From([Space(1, 600), Space(2, 604), Space(3, 665.4), Space(5, 610), Space(1, 100, drive: "C:\\"), Space(2, 109.9, drive: "C:\\")]);
        Assert.Equal(2, changes.Count);
        Assert.Equal((new DateTime(2026, 9, 3), ChangeKind.Storage, "61 GB more in use on D:", "604 GB to 665 GB in use"), (changes[0].Time, changes[0].Kind, changes[0].Title, changes[0].Detail));
        Assert.Equal((new DateTime(2026, 9, 5), "55 GB freed on D:"), (changes[1].Time, changes[1].Title));
        Assert.False(changes[0].IsSystemLevel);
    }

    [Fact]
    public void Exactly_the_least_counts_and_a_drive_of_another_size_isnt_compared()
    {
        Assert.Single(StorageChanges.From([Space(1, 100), Space(2, 100 + StorageChanges.MinGb)]));
        Assert.Empty(StorageChanges.From([Space(1, 100), Space(2, 99.9 + StorageChanges.MinGb)]));
        Assert.Empty(StorageChanges.From([Space(1, 400, total: 465), Space(2, 900, total: 1863)]));
        Assert.Empty(StorageChanges.From([]));
        Assert.Empty(StorageChanges.From([Space(1, 400)]));
    }

    // ---- This machine ----

    [Fact]
    public void This_PCs_inventory_reads_quickly_and_twice_the_same()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = Inventory.Read();
        var second = Inventory.Read();
        clock.Stop();

        Assert.NotEmpty(first[Inventory.App]);
        Assert.NotEmpty(first[Inventory.Disk]);
        Assert.Contains(Inventory.Windows, first.Keys);
        Assert.Contains(Inventory.Cpu, first.Keys);
        Assert.All(first.Values.SelectMany(v => v), i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
        Assert.All(first, kind => Assert.Equal(kind.Value.Count, kind.Value.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        // Nothing was installed in between: no changes of any kind.
        Assert.All(first.Keys, kind => Assert.Empty(Inventory.Diff(kind, first[kind], second[kind], At)));
        Assert.False(string.IsNullOrEmpty(Inventory.Owner()));
        Assert.True(clock.ElapsedMilliseconds < 4000, $"two readings took {clock.ElapsedMilliseconds} ms");
    }
}
