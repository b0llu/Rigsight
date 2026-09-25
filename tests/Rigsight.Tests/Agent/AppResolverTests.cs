using Rigsight.Agent.Tracking;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>Exe names to app records: created once, names fixed up, display names and categories from settings.</summary>
public sealed class AppResolverTests : IDisposable
{
    private readonly RigsightDb _db = RigsightDb.OpenWriter(Path.Combine(TestEnvironment.NewFolder("apps"), "rigsight.db"));

    public void Dispose() => _db.Dispose();

    [Fact]
    public void An_app_is_created_once_whatever_the_case()
    {
        var apps = new AppResolver(_db);
        var a = apps.Get("Game.exe", 0);
        var b = apps.Get("GAME.EXE", 0);
        Assert.Same(a, b);
        var row = Assert.Single(_db.LoadApps());
        Assert.Equal(a.Id, row.Id);
        Assert.Equal("Game.exe", row.Exe);
        Assert.Equal("Game", row.Name);
        Assert.Null(row.Path);
        Assert.Single(apps.All);
    }

    [Fact]
    public void Apps_are_loaded_back_after_a_restart()
    {
        var first = new AppResolver(_db);
        long id = first.Get("tool.exe", 0).Id;
        first.MarkAsGame(first.Get("tool.exe", 0));
        var again = new AppResolver(_db);
        var app = again.Get("TOOL.exe", 0);
        Assert.Equal(id, app.Id);
        Assert.Equal(AppCategory.Game, app.AutoCategory);
        Assert.Single(_db.LoadApps());
    }

    [Fact]
    public void Describe_names_an_app_without_recording_it()
    {
        var apps = new AppResolver(_db);
        var (name, path) = apps.Describe("someservice.exe", 0);
        Assert.Equal("Someservice", name);
        Assert.Null(path);
        Assert.Empty(_db.LoadApps());
        Assert.Empty(apps.All);
    }

    [Fact]
    public void Describe_uses_the_recorded_app_when_there_is_one()
    {
        _db.UpsertApp("eldenring.exe", "ELDEN RING™", @"C:\Games\ELDEN RING\eldenring.exe", AppCategory.Game);
        var apps = new AppResolver(_db);
        Assert.Equal(("ELDEN RING™", @"C:\Games\ELDEN RING\eldenring.exe"), apps.Describe("EldenRing.exe", 0));
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void A_running_process_gives_its_path_and_description()
    {
        // This test process: a real path, read without side effects.
        var apps = new AppResolver(_db);
        string exe = Path.GetFileName(Environment.ProcessPath)!;
        var (name, path) = apps.Describe(exe, Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(name));

        var app = apps.Get(exe, Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, app.Path, ignoreCase: true);
        Assert.Equal(Environment.ProcessPath, _db.LoadApps().Single().Path, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void A_path_that_no_longer_exists_is_replaced_by_the_running_copy()
    {
        // Discord and others update themselves into a new folder ("app-1.0.9185"): the old path loses the icon.
        string exe = Path.GetFileName(Environment.ProcessPath)!;
        _db.UpsertApp(exe, "Tests", @"C:\Gone\app-0.0.1\" + exe, AppCategory.Other);
        var apps = new AppResolver(_db);
        var app = apps.Get(exe, Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, app.Path, ignoreCase: true);
        Assert.Equal(Environment.ProcessPath, _db.LoadApps().Single().Path, ignoreCase: true);
    }

    [Fact]
    public void Without_a_process_the_saved_path_is_kept()
    {
        _db.UpsertApp("gone.exe", "Gone", @"C:\Gone\gone.exe", AppCategory.Other);
        var apps = new AppResolver(_db);
        Assert.Equal(@"C:\Gone\gone.exe", apps.Get("gone.exe", 0).Path);
        Assert.Equal(@"C:\Gone\gone.exe", apps.Get("gone.exe", 4).Path); // System's pid
    }

    [Fact]
    public void Generic_and_outdated_names_are_fixed_when_loaded()
    {
        _db.UpsertApp("screenclippinghost.exe", "Microsoft® Windows® Operating System", null, AppCategory.Other);
        _db.UpsertApp("helper.exe", "Electron", null, AppCategory.Other);
        _db.UpsertApp("svchost.exe", "Host Process for Windows Services", null, AppCategory.System);
        _db.UpsertApp("mygame.exe", "My Game", null, AppCategory.Game);
        var apps = new AppResolver(_db);
        var names = apps.All.ToDictionary(a => a.Exe, a => a.Name);
        Assert.Equal("Snipping Tool", names["screenclippinghost.exe"]);
        Assert.Equal("Helper", names["helper.exe"]);
        Assert.Equal("Windows service", names["svchost.exe"]);
        Assert.Equal("My Game", names["mygame.exe"]);
        // Saved, so the app's pages show them too.
        var saved = _db.LoadApps().ToDictionary(a => a.Exe, a => a.Name);
        Assert.Equal("Snipping Tool", saved["screenclippinghost.exe"]);
        Assert.Equal("Helper", saved["helper.exe"]);
        Assert.Equal(AppCategory.System, _db.LoadApps().Single(a => a.Exe == "svchost.exe").Category);
    }

    [Fact]
    public void Marking_as_a_game_is_saved()
    {
        var apps = new AppResolver(_db);
        var app = apps.Get("indie.exe", 0);
        Assert.Equal(AppCategory.Other, app.AutoCategory);
        apps.MarkAsGame(app);
        Assert.Equal(AppCategory.Game, app.AutoCategory);
        Assert.Equal(AppCategory.Game, _db.LoadApps().Single().Category);
    }

    [Fact]
    public void Display_name_and_category_follow_the_users_choices()
    {
        var app = new AppInfo { Id = 1, Exe = "code.exe", Name = "Visual Studio Code", AutoCategory = AppCategory.Development };
        var plain = new RigsightSettings();
        Assert.Equal("Visual Studio Code", AppResolver.DisplayName(app, plain));
        Assert.Equal(AppCategory.Development, AppResolver.Category(app, plain));

        var chosen = new RigsightSettings();
        chosen.AppNames["CODE.EXE"] = "Editor";
        chosen.AppCategories["Code.exe"] = AppCategory.Productivity;
        Assert.Equal("Editor", AppResolver.DisplayName(app, chosen));
        Assert.Equal(AppCategory.Productivity, AppResolver.Category(app, chosen));

        // Settings that went through a save and load keep ignoring case.
        var loaded = SettingsStore.Deserialize(SettingsStore.Serialize(chosen));
        Assert.Equal("Editor", AppResolver.DisplayName(app, loaded));
        Assert.Equal(AppCategory.Productivity, AppResolver.Category(app, loaded));
    }

    [Theory]
    [InlineData("chrome.exe", AppCategory.Browser)]
    [InlineData("steam.exe", AppCategory.Launcher)]
    [InlineData("discord.exe", AppCategory.Communication)]
    [InlineData("explorer.exe", AppCategory.System)]
    [InlineData("whatever.exe", AppCategory.Other)]
    public void New_apps_are_classified(string exe, AppCategory expected) =>
        Assert.Equal(expected, new AppResolver(_db).Get(exe, 0).AutoCategory);
}
