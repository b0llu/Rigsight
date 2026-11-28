using Rigsight.Agent.Native;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Tracking;

internal sealed class AppInfo
{
    public long Id { get; init; }
    public required string Exe { get; init; }
    public required string Name { get; set; }
    public string? Path { get; set; }
    /// <summary>Automatically detected category (user overrides are applied on top).</summary>
    public AppCategory AutoCategory { get; set; }
}

/// <summary>Maps exe names to database app records, resolving friendly names and categories once.</summary>
internal sealed class AppResolver
{
    private readonly RigsightDb db;
    private readonly Dictionary<string, AppInfo> _byExe = new(StringComparer.OrdinalIgnoreCase);

    public AppResolver(RigsightDb db)
    {
        this.db = db;
        Reload();
    }

    private readonly Dictionary<string, (string Name, string? Path, AppCategory Category)> _described = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AppInfo> All => _byExe.Values;

    /// <summary>The app record when the exe has one already (none is made).</summary>
    public AppInfo? Known(string exe) => _byExe.GetValueOrDefault(exe);

    /// <summary>Returns the app record, creating it in the database the first time an exe is seen.</summary>
    public AppInfo Get(string exe, int pid)
    {
        if (_byExe.TryGetValue(exe, out var app))
        {
            CheckPath(app, pid);
            if (app.Path is null && pid > 4 && Win32.ProcessPath(pid) is { } p)
            {
                app.Path = p;
                app.Name = AppCatalog.ResolveName(exe, p);
                var found = AppCatalog.Decide(exe, p);
                app.AutoCategory = found.Category;
                db.UpsertApp(app.Exe, app.Name, app.Path, app.AutoCategory);
                Log.Write("apps", $"{app.Name} ({exe}): {found.Category}, {found.Why}");
            }
            else CheckKind(app);
            return app;
        }

        var path = pid > 4 ? Win32.ProcessPath(pid) : null;
        var name = AppCatalog.ResolveName(exe, path);
        var verdict = AppCatalog.Decide(exe, path);
        var id = db.UpsertApp(exe, name, path, verdict.Category);
        app = new AppInfo { Id = id, Exe = exe, Name = name, Path = path, AutoCategory = verdict.Category };
        _byExe[exe] = app;
        _kindChecked.Add(exe);
        // Once an app (its record is made the first time it's seen): which kind, and what said so.
        Log.Write("apps", $"New app {name} ({exe}{(path is null ? ", no path" : "")}): {verdict.Category}, {verdict.Why}");
        return app;
    }

    private readonly HashSet<string> _kindChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Once a run per app, the first time it's seen running: an app filed as Other by an older version, when what the
    /// program says of itself now names its kind (a browser, a chat app, a Store app's package). Done here rather than
    /// as the agent starts, as it reads each program's file: only the apps running are read. Game and every other kind
    /// stay as they were (a game learned from play stays a game).
    /// </summary>
    private void CheckKind(AppInfo app)
    {
        if (app.AutoCategory != AppCategory.Other || app.Path is null || !_kindChecked.Add(app.Exe)) return;
        var now = AppCatalog.Decide(app.Exe, app.Path);
        if (now.Category == AppCategory.Other) return;
        try
        {
            db.Recategorize(app.Id, now.Category);
            app.AutoCategory = now.Category;
            Log.Write("apps", $"{app.Name} ({app.Exe}): filed as Other, now {now.Category} ({now.Why}); its sessions follow");
        }
        catch (Exception ex) { Log.Error("apps", ex); }
    }

    /// <summary>
    /// The app record for a Windows service running in its own service host ("svchost.exe:DoSvc"), named after the
    /// service ("Delivery Optimization") rather than the host, made the first time it's seen.
    /// </summary>
    public AppInfo GetService(string service, int pid)
    {
        string exe = "svchost.exe:" + service;
        if (_byExe.TryGetValue(exe, out var app)) return app;
        var path = pid > 4 ? Win32.ProcessPath(pid) : null;
        var name = AppCatalog.KnownName(exe) ?? ServiceNames.Display(service);
        var id = db.UpsertApp(exe, name, path, AppCategory.System);
        app = new AppInfo { Id = id, Exe = exe, Name = name, Path = path, AutoCategory = AppCategory.System };
        _byExe[exe] = app;
        return app;
    }

    /// <summary>
    /// Name, path and kind (as found from the program; the person's choice goes on top) for display only: doesn't create
    /// a database record. An app without one has its kind worked out once a run, as its name is.
    /// </summary>
    public (string Name, string? Path, AppCategory Category) Describe(string exe, int pid)
    {
        if (_byExe.TryGetValue(exe, out var app))
        {
            CheckPath(app, pid);
            return (app.Name, app.Path, app.AutoCategory);
        }
        if (_described.TryGetValue(exe, out var d)) return d;
        var path = pid > 4 ? Win32.ProcessPath(pid) : null;
        d = (AppCatalog.ResolveName(exe, path), path, AppCatalog.Classify(exe, path));
        _described[exe] = d;
        return d;
    }

    private readonly HashSet<string> _pathChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Once a run per app: apps that update themselves into a new folder (Discord's "app-1.0.9258") leave the saved
    /// path pointing at a folder that's gone, and with it the icon. Takes the running copy's path instead.
    /// </summary>
    private void CheckPath(AppInfo app, int pid)
    {
        if (app.Path is null || pid <= 4 || !_pathChecked.Add(app.Exe) || File.Exists(app.Path)) return;
        if (Win32.ProcessPath(pid) is not { } path || path.Equals(app.Path, StringComparison.OrdinalIgnoreCase)) return;
        app.Path = path;
        try { db.UpsertApp(app.Exe, app.Name, app.Path, app.AutoCategory); }
        catch (Exception ex) { Log.Error("apps", ex); }
    }

    public void MarkAsGame(AppInfo app)
    {
        app.AutoCategory = AppCategory.Game;
        db.UpsertApp(app.Exe, app.Name, app.Path, app.AutoCategory);
    }

    public static string DisplayName(AppInfo app, RigsightSettings s) =>
        s.AppNames.TryGetValue(app.Exe, out var alias) ? alias : app.Name;

    public static AppCategory Category(AppInfo app, RigsightSettings s) =>
        s.AppCategories.TryGetValue(app.Exe, out var c) ? c : app.AutoCategory;

    public void Reload()
    {
        _byExe.Clear();
        foreach (var a in db.LoadApps())
        {
            // Names saved by older versions ("Microsoft® Windows® Operating System" for a screenshot, or a game's file
            // name where its library now gives the real one, or "Grounded2" from the folder "Grounded 2", or a game
            // named after its maker's launcher) get fixed once.
            var name = a.Name;
            if (AppCatalog.KnownName(a.Exe) is { } known && known != name) name = known;
            else if (AppCatalog.IsGenericName(name) || name == AppCatalog.FallbackName(a.Exe) && AppCatalog.GameName(a.Path) is not null)
                name = AppCatalog.ResolveName(a.Exe, a.Path);
            else if (AppCatalog.GameBehindLauncher(a.Exe, name, a.Path) is { } game) name = game;
            // A launcher an older version named after the game folder it sits in (RockstarService.exe was "Launcher").
            else if (AppCatalog.DressedAsGame(a.Exe, name, a.Path)) name = AppCatalog.ResolveName(a.Exe, a.Path);
            else name = AppCatalog.Spaced(name, a.Path);
            if (name != a.Name)
            {
                try { db.UpsertApp(a.Exe, name, a.Path, a.Category); }
                catch (Exception ex) { Log.Error("apps", ex); }
            }
            // Filed wrongly by an older version where something now says so for certain: a launcher lists the game's
            // folder (Rocket League in E:\rocketleague was "Other"), or it's a launcher's own program (by name and folder,
            // now checked before the game folders: Rockstar's Launcher.exe was a game), a crash reporter or an anti-cheat
            // starter that sat in a game folder. Its saved sessions follow.
            var category = a.Category;
            if (AppCatalog.Reclassified(a.Exe, a.Path, a.Category) is { } now)
            {
                try
                {
                    db.Recategorize(a.Id, now);
                    Log.Write("apps", $"{name} ({a.Exe}): filed as {a.Category}, now {now} ({AppCatalog.Decide(a.Exe, a.Path, readFile: false).Why}); its sessions follow");
                    category = now;
                }
                catch (Exception ex) { Log.Error("apps", ex); }
            }
            _byExe[a.Exe] = new AppInfo { Id = a.Id, Exe = a.Exe, Name = name, Path = a.Path, AutoCategory = category };
        }
    }
}
