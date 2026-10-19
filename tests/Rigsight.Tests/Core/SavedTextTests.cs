using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// Lines saved in English (change titles, crash details, repair summaries) shown in the language in use: each saved form
/// is said through its template, old lines alike; nothing changes without a pack; and every form the code saves is known.
/// </summary>
public sealed class SavedTextTests
{
    /// <summary>A pack that marks every template it knows, so a test sees which one was used and with what values.</summary>
    private static LanguagePack Marked()
    {
        var strings = SavedText.All.Select(p => p.Template).Distinct()
            .ToDictionary(t => t, t => "«" + t + "»");
        strings["Fast Startup"] = "Inicio rápido";
        strings["Balanced"] = "Equilibrado";
        strings["High performance"] = "Alto rendimiento";
        return LanguagePack.Read(JsonSerializer.Serialize(new { code = "es", strings }));
    }

    public static TheoryData<string, string> Saved() => new()
    {
        { "Copilot updated to 26.01", "«Copilot updated to 26.01»" },
        { "Discord updated", "«Discord updated»" },
        { "Mage Arena installed", "«Mage Arena installed»" },
        { "Mage Arena removed", "«Mage Arena removed»" },
        { "NVIDIA graphics driver installed", "«NVIDIA graphics driver installed»" },
        { "NVIDIA graphics driver 616.92", "«NVIDIA graphics driver 616.92»" },
        { "Graphics driver 31.0.101", "«Graphics driver 31.0.101»" },
        { "Realtek audio driver 6.0.9", "«Realtek audio driver 6.0.9»" },
        { "Logitech device driver 1.2", "«Logitech device driver 1.2»" },
        { "PawnIO sensor driver 2.2.0", "«PawnIO sensor driver 2.2.0»" },
        { "NVIDIA display driver 32.0.15 (from Windows Update)", "«NVIDIA display driver 32.0.15 (from Windows Update)»" },
        { "Windows update KB5066835", "«Windows update KB5066835»" },
        { "Windows update", "«Windows update»" },
        { ".NET update KB5066000", "«.NET update KB5066000»" },
        { "Windows 11 updated to 25H2", "«Windows 11 updated to 25H2»" },
        { "BIOS updated to F64", "«BIOS updated to F64»" },
        { "Graphics card removed: NVIDIA GeForce RTX 3080 Ti", "«Graphics card removed: NVIDIA GeForce RTX 3080 Ti»" },
        { "Graphics card added: AMD Radeon", "«Graphics card added: AMD Radeon»" },
        { "Drive added: Samsung SSD 980", "«Drive added: Samsung SSD 980»" },
        { "Drive removed: Kingston A400", "«Drive removed: Kingston A400»" },
        { "Processor changed to AMD Ryzen 7 5700X3D", "«Processor changed to AMD Ryzen 7 5700X3D»" },
        { "Memory changed to 32 GB", "«Memory changed to 32 GB»" },
        { "Motherboard changed to B450 AORUS PRO", "«Motherboard changed to B450 AORUS PRO»" },
        { "Power plan changed to High performance", "«Power plan changed to Alto rendimiento»" },
        { "Fast Startup turned off", "«Inicio rápido turned off»" },
        { "Game Mode turned on", "«Game Mode turned on»" },
        { "Steam now starts with Windows", "«Steam now starts with Windows»" },
        { "Steam no longer starts with Windows", "«Steam no longer starts with Windows»" },
        { "Steam turned off at startup", "«Steam turned off at startup»" },
        { "Edge left startup and came straight back", "«Edge left startup and came straight back»" },
        { "Edge was turned on at startup and straight back off", "«Edge was turned on at startup and straight back off»" },
        { "Chrome ended from Processes", "«Chrome ended from Processes»" },
        { "GPU disconnected for 1 minute: AMD Radeon", "«GPU disconnected for 1 minute: AMD Radeon»" },
        { "GPU disconnected for 4 minutes: AMD Radeon", "«GPU disconnected for 4 minutes: AMD Radeon»" },
        { "Fan speeds changed while the PC was off", "«Fan speeds changed while the PC was off»" },
        { "12 GB added to the C drive", "«12 GB added to the C drive»" },
        { "1,204 GB freed on the D drive", "«1,204 GB freed on the D drive»" },
        { "Display driver stopped responding and has recovered", "«Display driver stopped responding and has recovered»" },
        { "Noticed at startup 19-10-2026 9:14", "«Noticed at startup 19-10-2026 9:14»" },
        { "Windows reported that the graphics card stopped answering (LiveKernelEvent 141)", "«Windows reported that the graphics card stopped answering (LiveKernelEvent 141)»" },
        { "Drive C: needs 40 MB free for the repair. It has 12 MB.", "«Drive C: needs 40 MB free for the repair. It has 12 MB.»" },
        { "Kept 3 of 5 days in full. Nothing was lost. The damaged file is kept.", "«Kept 3 of 5 days in full.» «Nothing was lost.» «The damaged file is kept.»" },
        { "Everything after Fri 3 Oct, 2:10 PM couldn't be read. The damaged file is kept.", "««Everything after Fri 3 Oct, 2:10 PM» couldn't be read.» «The damaged file is kept.»" },
    };

    [Theory]
    [MemberData(nameof(Saved))]
    public void A_saved_line_is_said_through_its_template(string saved, string expected)
    {
        // The marked pack says each template between « », filled with the saved line's own values.
        using var _ = Lang.UseHere(Marked());
        Assert.Equal(expected, SavedText.Show(saved));
    }

    [Theory]
    [MemberData(nameof(Saved))]
    public void Without_a_pack_a_saved_line_is_shown_exactly_as_saved(string saved, string _)
    {
        using var __ = Lang.UseHere(null);
        Assert.Same(saved, SavedText.Show(saved));
    }

    [Fact]
    public void A_line_no_pattern_knows_is_shown_as_saved()
    {
        using var _ = Lang.UseHere(Marked());
        Assert.Equal("Something nobody wrote a pattern for", SavedText.Show("Something nobody wrote a pattern for"));
        Assert.Equal("", SavedText.Show(""));
        Assert.Equal("", SavedText.Show(null));
    }

    [Fact]
    public void A_template_is_filled_with_the_lines_own_values()
    {
        var pack = LanguagePack.Read("""
            { "code": "es", "strings": {
                "{0} updated to {1}": "{0} actualizado a {1}",
                "{0} installed": "{0} instalado",
                "GPU disconnected for {0} minutes: {1}": "GPU desconectada durante {0} minutos: {1}",
                "{0} turned off": "{0} desactivado",
                "Fast Startup": "Inicio rápido",
                "Kept {0} of {1} days in full.": "Se conservaron {0} de {1} días completos.",
                "The damaged file is kept.": "Se conserva el archivo dañado."
            } }
            """);
        using var _ = Lang.UseHere(pack);
        Assert.Equal("Copilot actualizado a 26.01", SavedText.Show("Copilot updated to 26.01"));
        Assert.Equal("Mage Arena instalado", SavedText.Show("Mage Arena installed"));
        Assert.Equal("GPU desconectada durante 4 minutos: AMD Radeon", SavedText.Show("GPU disconnected for 4 minutes: AMD Radeon"));
        Assert.Equal("Inicio rápido desactivado", SavedText.Show("Fast Startup turned off"));
        Assert.Equal("Se conservaron 3 de 5 días completos. Nothing was lost. Se conserva el archivo dañado.",
            SavedText.Show("Kept 3 of 5 days in full. Nothing was lost. The damaged file is kept."));
        // An app called like one of the app's own words keeps its name: only closed sets of words are translated inside.
        Assert.Equal("Fast Startup instalado", SavedText.Show("Fast Startup installed"));
    }

    [Fact]
    public void A_changes_name_in_a_crash_finding_is_an_installs_name_or_the_title_as_shown()
    {
        using var _ = Lang.UseHere(LanguagePack.Read("""{ "code": "es", "strings": { "{0} updated to {1}": "{0} actualizado a {1}" } }"""));
        Assert.Equal("NVIDIA graphics driver", SavedText.ChangeName(new SystemChange(DateTime.Now, ChangeKind.Driver, "NVIDIA graphics driver installed")));
        Assert.Equal("Copilot actualizado a 26.01", SavedText.ChangeName(new SystemChange(DateTime.Now, ChangeKind.AppUpdated, "Copilot updated to 26.01")));
        Assert.Equal("Copilot actualizado a 26.01", new SystemChange(DateTime.Now, ChangeKind.AppUpdated, "Copilot updated to 26.01").Shown);
    }

    // ── Every form the code saves is known ──────────────────────────────

    [Fact]
    public void Every_change_title_the_inventory_writes_is_known()
    {
        var at = DateTime.Now;
        var titles = new List<string>();
        void Diff(string kind, InventoryItem[] before, InventoryItem[] after) => titles.AddRange(Inventory.Diff(kind, before, after, at).Select(c => c.Title));
        InventoryItem I(string kind, string key, string name, string value) => new(kind, key, name, value);

        foreach (var kind in new[] { Inventory.App, Inventory.Startup, Inventory.Gpu, Inventory.Disk, Inventory.Game })
        {
            Diff(kind, [], [I(kind, "a", "Mage Arena", Inventory.On)]);
            Diff(kind, [I(kind, "a", "Mage Arena", Inventory.On)], []);
            if (kind == Inventory.Startup) continue; // on or off only, below
            Diff(kind, [I(kind, "a", "Mage Arena", "1.0")], [I(kind, "a", "Mage Arena", "1.1")]);
            Diff(kind, [I(kind, "a", "Mage Arena", "1.0")], [I(kind, "a", "Mage Arena", "")]);
        }
        Diff(Inventory.Startup, [I(Inventory.Startup, "s", "Steam", Inventory.On)], [I(Inventory.Startup, "s", "Steam", Inventory.Off)]);
        Diff(Inventory.Startup, [I(Inventory.Startup, "s", "Steam", Inventory.Off)], [I(Inventory.Startup, "s", "Steam", Inventory.On)]);
        foreach (var kind in new[] { Inventory.GpuDriver, Inventory.Cpu, Inventory.Ram, Inventory.Board, Inventory.Bios, Inventory.Windows })
            Diff(kind, [I(kind, "k", kind == Inventory.GpuDriver ? "NVIDIA graphics driver" : "Windows 11", "1")], [I(kind, "k", kind == Inventory.GpuDriver ? "NVIDIA graphics driver" : "Windows 11", "2")]);
        Diff(Inventory.Setting, [I(Inventory.Setting, Inventory.PowerPlan, "Power plan", "Balanced")], [I(Inventory.Setting, Inventory.PowerPlan, "Power plan", "High performance")]);
        foreach (var name in new[] { "Fast Startup", "Hardware-accelerated GPU scheduling", "Game Mode", "Memory integrity" })
        {
            Diff(Inventory.Setting, [I(Inventory.Setting, name, name, Inventory.On)], [I(Inventory.Setting, name, name, Inventory.Off)]);
            Diff(Inventory.Setting, [I(Inventory.Setting, name, name, Inventory.Off)], [I(Inventory.Setting, name, name, Inventory.On)]);
        }
        titles.AddRange(new[] { "Steam no longer starts with Windows", "Steam now starts with Windows", "Steam turned off at startup", "Steam turned on at startup" }
            .Select(RigsightDb.FlickerTitle));

        Assert.NotEmpty(titles);
        Assert.All(titles, t => Assert.True(SavedText.IsKnown(t), $"SavedText has no pattern for the saved title \"{t}\""));
    }

    [Fact]
    public void Every_title_read_from_Windows_logs_is_known()
    {
        string[] titles =
        [
            ChangeLogReader.Describe("2026-10 Cumulative Update for Windows 11 Version 25H2 for x64-based Systems (KB5066835)"),
            ChangeLogReader.Describe("Cumulative Update for Windows 11"),
            ChangeLogReader.Describe("2026-10 Cumulative Update for .NET Framework 3.5 and 4.8.1 (KB5066000)"),
            ChangeLogReader.Describe(".NET 8.0.20 Security Update"),
            ChangeLogReader.Describe("NVIDIA - Display - 32.0.15.8097"),
            ChangeLogReader.Describe("LG Electronics Inc. Extension Driver Update (1.1.2026.6241)"),
            TaskEnded.Parse(TaskEnded.Format("Chrome", "chrome.exe", 100), DateTime.Now)!.Title,
        ];
        Assert.All(titles, t => Assert.True(SavedText.IsKnown(t), $"SavedText has no pattern for \"{t}\""));
    }

    [Fact]
    public void Every_repair_message_is_known()
    {
        var day = new DateTime(2026, 10, 3, 14, 10, 0);
        string[] lines =
        [
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.NothingSaved }),
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.Repaired, Days = 5, DaysWhole = 3 }),
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.Repaired, TablesHit = ["apps"] }),
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.Repaired, Days = 5, DaysWhole = 4, Lost = [new(day, day.AddHours(1))] }),
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.Repaired, Lost = [new(day, day.AddDays(1)), new(day, null)] }),
            HistoryRepair.Summary(new HistoryReport { Outcome = HistoryOutcome.Repaired, Lost = [new(null, day), new(null, null), new(day, null)] }),
            "Drive C: needs 40 MB free for the repair. It has 12 MB.",
            "The repair didn't finish: disk full",
            "The history file stayed in use: locked",
        ];
        Assert.All(lines, t => Assert.True(SavedText.IsKnown(t), $"SavedText has no pattern for \"{t}\""));
    }

    /// <summary>
    /// The places that write a line someone later sees. A new one fails here until its forms are in SavedText (and in
    /// one of the tests above): otherwise it would show in English in every other language.
    /// </summary>
    [Fact]
    public void No_new_place_saves_a_shown_line_without_a_pattern()
    {
        var src = Path.Combine(Repo.Root, "src");
        int Count(string file, string pattern) => Regex.Matches(File.ReadAllText(Path.Combine(src, file)), pattern).Count;

        var found = new Dictionary<string, int>
        {
            ["new SystemChange( anywhere"] = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Sum(f => Regex.Matches(File.ReadAllText(f), @"new SystemChange\(").Count),
            ["Inventory.Diff titles"] = Count(@"Rigsight.Core\Stability\Inventory.cs", @"Add\(ChangeKind\."),
            ["crash event details"] = Count(@"Rigsight.Core\Stability\CrashLogReader.cs", @"\bDetail = "),
            ["repair waiting reasons"] = Count(@"Rigsight.Core\Data\HistoryRepair.cs", @"WaitingReason = |holdUp = \$"),
        };
        var expected = new Dictionary<string, int>
        {
            // ChangeLogReader ×2, FanSettings, Inventory, StorageChanges, TaskEnded, RigsightDb.Changes ×2 (one reads saved rows back).
            ["new SystemChange( anywhere"] = 8,
            ["Inventory.Diff titles"] = 19,
            ["crash event details"] = 3,
            ["repair waiting reasons"] = 4,
        };
        foreach (var (what, n) in expected)
            Assert.True(found[what] == n, $"{what}: {found[what]} places, {n} known. A new saved line? Add its forms to SavedText and a case to these tests, then update the count.");
    }
}
