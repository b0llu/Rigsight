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

    private readonly Dictionary<string, (string Name, string? Path)> _described = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AppInfo> All => _byExe.Values;

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
                app.AutoCategory = AppCatalog.Classify(exe, p);
                db.UpsertApp(app.Exe, app.Name, app.Path, app.AutoCategory);
            }
            return app;
        }

        var path = pid > 4 ? Win32.ProcessPath(pid) : null;
        var name = AppCatalog.ResolveName(exe, path);
        var category = AppCatalog.Classify(exe, path);
        var id = db.UpsertApp(exe, name, path, category);
        app = new AppInfo { Id = id, Exe = exe, Name = name, Path = path, AutoCategory = category };
        _byExe[exe] = app;
        return app;
    }

    /// <summary>Name and path for display only (doesn't create a database record).</summary>
    public (string Name, string? Path) Describe(string exe, int pid)
    {
        if (_byExe.TryGetValue(exe, out var app))
        {
            CheckPath(app, pid);
            return (app.Name, app.Path);
        }
        if (_described.TryGetValue(exe, out var d)) return d;
        var path = pid > 4 ? Win32.ProcessPath(pid) : null;
        d = (AppCatalog.ResolveName(exe, path), path);
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
            // Names saved by older versions ("Microsoft® Windows® Operating System" for a screenshot) get fixed once.
            var name = a.Name;
            if (AppCatalog.KnownName(a.Exe) is { } known && known != name) name = known;
            else if (AppCatalog.IsGenericName(name)) name = AppCatalog.ResolveName(a.Exe, a.Path);
            if (name != a.Name)
            {
                try { db.UpsertApp(a.Exe, name, a.Path, a.Category); }
                catch (Exception ex) { Log.Error("apps", ex); }
            }
            _byExe[a.Exe] = new AppInfo { Id = a.Id, Exe = a.Exe, Name = name, Path = a.Path, AutoCategory = a.Category };
        }
    }
}
