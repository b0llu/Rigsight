using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Which tests does a change need? Compares the working tree with the last release (the newest v* tag, which passed
// its checks) and answers with one of three levels, and why:
//   none      only the site, notes or other files the programs don't use changed: build and changed tests only
//   selected  the test classes that reach the changed code (through any chain of classes using classes), plus the
//             page renders when anything on screen changed, and a quick end-to-end run when the programs changed
//   full      something shared or hard to follow changed (settings, the pipe protocol, history storage, themes, the
//             agent's main loop, startup, build files, test helpers): everything, as before a big release
// The dependency map is read from the code each time (declared types, and every name used), so it can't go stale;
// it over-selects rather than under-selects: a class name used anywhere counts, whatever it refers to.
//
//   Rigsight.Impact [--base <git ref>] [--json <file>] [--files a.cs,b.xaml]   (--files: what if only these changed)
//   Rigsight.Impact --split <file> [--parts <n>] [--all]
//     How to run the test classes as several processes side by side: the classes (the ones the changes need, or every
//     one with --all) dealt into up to <n> parts of about the same length, going by how long each took in the last
//     run that kept its results, and apart from them the classes marked [Trait("Alone", …)], which measure time or
//     use something the whole PC shares and run on their own afterwards.
var arg = (string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
string root = FindRoot();
string baseRef = arg("--base") ?? Git(root, "describe", "--tags", "--abbrev=0", "--match", "v*").Trim();

var changed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
if (arg("--files") is { } what)
{
    // "What if only these changed?" (comma-separated paths)
    foreach (var f in what.Split(',')) Add(f);
    baseRef = "(given files)";
}
else
{
    foreach (var line in Git(root, "diff", "--name-only", baseRef).Split('\n')) Add(line);
    foreach (var line in Git(root, "ls-files", "--others", "--exclude-standard").Split('\n')) Add(line);
}
void Add(string line) { if (line.Trim() is { Length: > 0 } f) changed.Add(f.Replace('\\', '/')); }

var result = args.Contains("--all") ? new Decision("full", ["everything was asked for"], [], "full", true) : Decide(root, changed);
if (arg("--split") is { } splitFile)
{
    var graph = Graph.Build(root);
    var wanted = result.Level == "full" ? graph.AllTestClasses.ToList() : result.Classes;
    var split = Split.Deal(wanted, graph.Alone, graph.OneAtATime, Split.LastTimes(root), int.TryParse(arg("--parts"), out int n) ? n : 4);
    File.WriteAllText(splitFile, JsonSerializer.Serialize(new { split.Parts, split.Alone, split.Seconds, split.AloneSeconds }, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Contains("--all"))
    {
        Console.WriteLine($"{wanted.Count} test classes in {split.Parts.Count} part(s) of about {string.Join(", ", split.Seconds.Select(s => $"{s:0} s"))}; {split.Alone.Count} alone afterwards (about {split.AloneSeconds:0} s)");
        return 0;
    }
}
Console.WriteLine($"Changes since {baseRef}: {changed.Count} file(s)");
foreach (var f in changed.Take(40)) Console.WriteLine($"  {f}");
if (changed.Count > 40) Console.WriteLine($"  …and {changed.Count - 40} more");
Console.WriteLine();
Console.WriteLine($"Level: {result.Level}");
foreach (var r in result.Reasons) Console.WriteLine($"  · {r}");
if (result.Level == "selected")
{
    Console.WriteLine($"  {result.Classes.Count} test class(es): {string.Join(", ", result.Classes.Select(c => c.Replace("Rigsight.Tests.", "")).Take(30))}{(result.Classes.Count > 30 ? ", …" : "")}");
    Console.WriteLine($"  end-to-end: {result.EndToEnd}");
}
if (arg("--why") is { } target)
{
    // How a change reaches a file: the chain of files and the type each one uses from the one before.
    var g = Graph.Build(root);
    g.Affected(changed.Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")));
    for (var f = target.Replace('\\', '/'); g.Via.TryGetValue(f, out var v); f = v.From)
        Console.WriteLine($"  {f}  uses  {v.Type}  (from {v.From})");
}
if (arg("--json") is { } json)
    File.WriteAllText(json, JsonSerializer.Serialize(new { baseRef, changed, result.Level, result.Reasons, result.Classes, result.EndToEnd, result.Installer },
        new JsonSerializerOptions { WriteIndented = true }));
return 0;

static Decision Decide(string root, IReadOnlyCollection<string> changed)
{
    var reasons = new List<string>();
    var full = new List<string>();
    var ignored = new List<string>();
    var code = new List<string>();
    bool screens = false, installer = false, e2eTool = false;

    foreach (var f in changed)
    {
        string name = Path.GetFileName(f);
        if (Risky(f) is { } why) full.Add($"{f}: {why}");
        else if (f.StartsWith("docs/") || f.StartsWith(".github/") || f.EndsWith(".md") || f is "LICENSE" or ".gitignore" || f.StartsWith("assets/") && !f.EndsWith(".ico"))
            ignored.Add(f);
        else if (f.StartsWith("installer/")) installer = true;
        else if (f.StartsWith("tools/") || f.StartsWith("tests/Rigsight.Impact/")) ignored.Add(f); // the checks themselves: they run anyway
        else if (f.StartsWith("tests/Rigsight.E2E/")) e2eTool = true;
        else if (f.EndsWith(".xaml") && f.StartsWith("src/Rigsight/")) { screens = true; code.Add(f); }
        else if (f.EndsWith(".cs")) code.Add(f);
        else if (f.StartsWith("tests/Rigsight.Tests/Fixtures/")) code.Add(f);
        else full.Add($"{f}: not something this check knows how to follow");
    }

    if (full.Count > 0)
        return new("full", [.. full.Select(r => "full run: " + r)], [], "full", installer || true);

    var graph = Graph.Build(root);
    var affected = graph.Affected(code.Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")));
    // A fixture file is read by the tests that name it.
    foreach (var fixture in code.Where(f => f.Contains("/Fixtures/"))) affected.UnionWith(graph.FilesMentioning(Path.GetFileName(fixture)));
    var classes = new SortedSet<string>(graph.TestClassesIn(affected), StringComparer.Ordinal);
    bool programs = affected.Any(f => f.StartsWith("src/"));

    if (screens || affected.Any(f => f.StartsWith("src/Rigsight/") && f.EndsWith(".xaml")))
    {
        // Whatever is on screen: every page rendered in both themes, sizes and scrolled.
        classes.Add("Rigsight.Tests.AppUi.AppWindowTests");
        reasons.Add("something on screen changed: every page is rendered in both themes");
    }
    classes.Add("Rigsight.Tests.SmokeTests");

    if (ignored.Count > 0) reasons.Add($"not used by the programs: {string.Join(", ", ignored.Take(8))}{(ignored.Count > 8 ? ", …" : "")}");
    if (installer) reasons.Add("the installer script changed: it's compiled to check it");
    foreach (var f in code.Where(f => f.StartsWith("src/")).Take(12))
        reasons.Add($"{f} → {graph.ReachSummary(f)}");

    string e2e = programs ? "quick" : e2eTool ? "quick" : "none";
    if (programs) reasons.Add("the programs changed: a quick end-to-end run of the Release build");
    else if (e2eTool) reasons.Add("the end-to-end check itself changed: it runs");
    string level = code.Count == 0 && !installer ? "none" : "selected";
    return new(level, reasons, [.. classes], e2e, installer);
}

// Shared by everything, or followed at run time rather than in code (JSON between the agent and the app, the
// database's contents, XAML resource lookups, startup), so a code map can't say what depends on them.
static string? Risky(string f) => f switch
{
    "Directory.Build.props" or "global.json" or "Rigsight.slnx" => "build settings",
    _ when f.EndsWith(".csproj") && !f.StartsWith("tests/Rigsight.Impact/") && !f.StartsWith("tests/Rigsight.E2E/") => "a project file",
    _ when f.EndsWith("app.manifest") => "how Windows starts a program",
    _ when f.StartsWith("src/Rigsight.Core/Settings/") => "settings (saved as JSON, read by both programs)",
    _ when f.StartsWith("src/Rigsight.Core/Protocol/") => "the messages between the agent and the app",
    _ when f.StartsWith("src/Rigsight.Core/Data/") => "history storage",
    _ when f.StartsWith("src/Rigsight/Themes/") => "the themes and shared styles every page uses",
    _ when f is "src/Rigsight/App.xaml" or "src/Rigsight/App.xaml.cs" or "src/Rigsight/Program.cs" or "src/Rigsight/MainWindow.xaml" or "src/Rigsight/MainWindow.xaml.cs" => "the app's startup and window",
    _ when f is "src/Rigsight.Agent/Program.cs" or "src/Rigsight.Agent/AgentContext.cs" => "the agent's startup and main loop",
    _ when f.StartsWith("src/Rigsight.Core/") && Path.GetFileName(f) is "RigsightPaths.cs" or "Log.cs" or "Units.cs" => "used by nearly everything",
    _ when f.StartsWith("tests/Rigsight.Tests/Support/") || f.StartsWith("tests/Rigsight.Tests/AppUi/AppHost") => "shared test helpers",
    _ when f.StartsWith("src/") && !(f.EndsWith(".cs") || f.EndsWith(".xaml")) => "a resource the programs load",
    _ => null,
};

static string FindRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "Rigsight.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? Directory.GetCurrentDirectory();
}

static string Git(string root, params string[] args)
{
    var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false })!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed");
    return output;
}

internal sealed record Decision(string Level, List<string> Reasons, List<string> Classes, string EndToEnd, bool Installer);

/// <summary>Test classes dealt into parts that take about as long as each other, and the ones that run alone.</summary>
internal sealed record Split(List<List<string>> Parts, List<string> Alone, List<double> Seconds, double AloneSeconds)
{
    /// <summary>A class not in the last results (a new one) is taken to be this long.</summary>
    private const double Unknown = 5;

    /// <summary>A part shorter than this isn't worth a process of its own (each starts the app's test host anew).</summary>
    private const double WorthAPart = 40;

    /// <summary>
    /// How much of a class's time a part waits for when the class runs beside others in its process (one in no
    /// collection does; the window tests share a collection and run one after another, so theirs counts in full).
    /// </summary>
    private const double BesideOthers = 0.25;

    public static Split Deal(IEnumerable<string> classes, IReadOnlySet<string> alone, IReadOnlySet<string> oneAtATime, IReadOnlyDictionary<string, double> times, int parts)
    {
        double Time(string c) => times.GetValueOrDefault(c, Unknown) * (oneAtATime.Contains(c) ? 1 : BesideOthers);
        var apart = classes.Where(alone.Contains).Order(StringComparer.Ordinal).ToList();
        var rest = classes.Where(c => !alone.Contains(c)).OrderByDescending(Time).ThenBy(c => c, StringComparer.Ordinal).ToList();
        parts = Math.Clamp((int)(rest.Sum(Time) / WorthAPart), 1, Math.Max(1, parts));
        var dealt = Enumerable.Range(0, parts).Select(_ => new List<string>()).ToList();
        var seconds = new double[parts];
        // Longest first, each to the part that is shortest so far.
        foreach (var c in rest)
        {
            int to = Array.IndexOf(seconds, seconds.Min());
            dealt[to].Add(c);
            seconds[to] += Time(c);
        }
        return new(dealt, apart, [.. seconds], apart.Sum(Time));
    }

    /// <summary>How long each test class took (its tests added up), from the newest results kept under TestResults.</summary>
    public static Dictionary<string, double> LastTimes(string root)
    {
        var times = new Dictionary<string, double>(StringComparer.Ordinal);
        string results = Path.Combine(root, "TestResults");
        if (!Directory.Exists(results)) return times;
        // The newest run that got as far as writing results (a part's file each, or one for the lot).
        foreach (var run in Directory.GetDirectories(results).OrderDescending())
        {
            var files = Directory.GetFiles(run, "*.trx", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                var classOf = new Dictionary<string, string>();
                foreach (Match m in Regex.Matches(text, @"<UnitTest [^>]*\bid=""([^""]+)""[^>]*>.*?<TestMethod [^>]*\bclassName=""([^""]+)""", RegexOptions.Singleline))
                    classOf[m.Groups[1].Value] = m.Groups[2].Value;
                foreach (Match m in Regex.Matches(text, @"<UnitTestResult [^>]*>"))
                {
                    var id = Regex.Match(m.Value, @"\btestId=""([^""]+)""");
                    var took = Regex.Match(m.Value, @"\bduration=""([^""]+)""");
                    if (!id.Success || !took.Success || !classOf.TryGetValue(id.Groups[1].Value, out var cls) || !TimeSpan.TryParse(took.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var t)) continue;
                    cls = cls.Replace('+', '.');
                    times[cls] = times.GetValueOrDefault(cls) + t.TotalSeconds;
                }
            }
            if (times.Count > 50) break; // a whole run, not a handful of classes
            times.Clear();
        }
        return times;
    }
}

/// <summary>Which files use which types, for the programs and the tests alike.</summary>
internal sealed class Graph
{
    private readonly Dictionary<string, HashSet<string>> _declares = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _uses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _testClasses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The test classes marked [Trait("Alone", …)]: they run on their own, after the rest.</summary>
    public HashSet<string> Alone { get; } = new(StringComparer.Ordinal);

    /// <summary>The test classes in a collection ([Collection("UI")]…): those of one collection run one after another.</summary>
    public HashSet<string> OneAtATime { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> AllTestClasses => _testClasses.Values.SelectMany(c => c).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _text = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _declaredIn = new(StringComparer.Ordinal);

    public static Graph Build(string root)
    {
        var g = new Graph();
        foreach (var dir in new[] { "src", "tests/Rigsight.Tests" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
                if (rel.EndsWith(".cs")) g.AddCode(rel, File.ReadAllText(path));
                else if (rel.EndsWith(".xaml")) g.AddXaml(rel, File.ReadAllText(path));
            }
        }
        return g;
    }

    private void AddCode(string file, string text)
    {
        _text[file] = text;
        var tree = CSharpSyntaxTree.ParseText(text);
        var rootNode = tree.GetRoot();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var tests = new List<string>();
        foreach (var type in rootNode.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // A test class is never used by other code, and a private nested type only inside its own file: neither
            // links files together.
            if (file.StartsWith("tests/Rigsight.Tests/") && type is ClassDeclarationSyntax c && IsTestClass(c))
            {
                tests.Add(FullName(c));
                if (c.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() == "Trait" && a.ArgumentList?.Arguments.FirstOrDefault()?.ToString() == "\"Alone\""))
                    Alone.Add(FullName(c));
                if (c.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() == "Collection")) OneAtATime.Add(FullName(c));
            }
            else if (!type.Modifiers.Any(SyntaxKind.PrivateKeyword))
                declared.Add(type.Identifier.Text);
        }
        foreach (var d in rootNode.DescendantNodes().OfType<DelegateDeclarationSyntax>()) declared.Add(d.Identifier.Text);
        // Names that can refer to a type: not a member after a dot ("row.App" is a property, not the App class).
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in rootNode.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name) continue;
            if (name.Ancestors().Any(a => a is UsingDirectiveSyntax or BaseNamespaceDeclarationSyntax && !a.DescendantNodes().OfType<MemberDeclarationSyntax>().Any(m => m.Span.Contains(name.Span)))) continue;
            if (name.Parent is QualifiedNameSyntax q && q.Left == name) continue; // "Rigsight.Core.Units": only Units names a type
            if (name.Parent is MemberBindingExpressionSyntax) continue;
            if (name.Parent is InvocationExpressionSyntax call && call.Expression == name) continue; // Pc(…) calls a method
            if (name.Parent is NameEqualsSyntax or NameColonSyntax) continue; // "Name = …" in initializers, named arguments
            if (name.Parent is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assign && assign.Left == name) continue;
            used.Add(name.Identifier.Text);
        }
        Index(file, declared, used);
        if (tests.Count > 0) _testClasses[file] = tests;
    }

    // Views: their class (x:Class), and the types they name ("m:SensorItem", x:Type, local:Control…).
    private void AddXaml(string file, string text)
    {
        _text[file] = text;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (Regex.Match(text, @"x:Class=""[\w.]*\.(\w+)""") is { Success: true } cls) declared.Add(cls.Groups[1].Value);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"\b(?:[a-z]+):([A-Z]\w+)")) used.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"\{x:Type\s+(?:\w+:)?(\w+)\}")) used.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"(?:Converter|Selector|Style|Template)=""\{StaticResource\s+(\w+)\}")) used.Add(m.Groups[1].Value);
        Index(file, declared, used);
    }

    private void Index(string file, HashSet<string> declared, HashSet<string> used)
    {
        _declares[file] = declared;
        used.ExceptWith(declared);
        _uses[file] = used;
        foreach (var t in declared)
        {
            if (!_declaredIn.TryGetValue(t, out var files)) _declaredIn[t] = files = new(StringComparer.OrdinalIgnoreCase);
            files.Add(file);
        }
    }

    private static bool IsTestClass(ClassDeclarationSyntax c) =>
        !c.Modifiers.Any(SyntaxKind.AbstractKeyword) &&
        c.Members.OfType<MethodDeclarationSyntax>().Any(m => m.AttributeLists.SelectMany(a => a.Attributes)
            .Any(a => a.Name.ToString() is "Fact" or "Theory" or "StaFact"));

    private static string FullName(ClassDeclarationSyntax c)
    {
        var parts = new List<string> { c.Identifier.Text };
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            if (p is ClassDeclarationSyntax outer) parts.Insert(0, outer.Identifier.Text + "+");
            if (p is BaseNamespaceDeclarationSyntax ns) { parts.Insert(0, ns.Name + "."); break; }
        }
        return string.Concat(parts).Replace("+", ".");
    }

    /// <summary>The changed files and every file that reaches one of their types through any chain of uses.</summary>
    /// <summary>For each reached file: the file and type it was reached through (for --why).</summary>
    public Dictionary<string, (string From, string Type)> Via { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Affected(IEnumerable<string> changedFiles)
    {
        Via.Clear();
        var affected = new HashSet<string>(changedFiles.Where(_declares.ContainsKey), StringComparer.OrdinalIgnoreCase);
        // A code-behind and its view are one class.
        foreach (var f in affected.ToList())
        {
            if (f.EndsWith(".xaml.cs") && _declares.ContainsKey(f[..^3])) affected.Add(f[..^3]);
            if (f.EndsWith(".xaml") && _declares.ContainsKey(f + ".cs")) affected.Add(f + ".cs");
        }
        // Affected types, with the files declaring them: a name only links to a type its own program can reach.
        var types = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Declare(string file)
        {
            foreach (var t in _declares[file])
            {
                if (!types.TryGetValue(t, out var files)) types[t] = files = [];
                files.Add(file);
            }
        }
        foreach (var f in affected) Declare(f);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (file, used) in _uses)
            {
                if (affected.Contains(file)) continue;
                string? via = null, type = null;
                foreach (var name in used)
                {
                    if (!types.TryGetValue(name, out var from)) continue;
                    via = from.FirstOrDefault(d => CanUse(file, d));
                    if (via is not null) { type = name; break; }
                }
                if (via is null) continue;
                affected.Add(file);
                Via[file] = (via, type!);
                Declare(file);
                grew = true;
            }
        }
        return affected;
    }

    /// <summary>
    /// Whether code in <paramref name="user"/> can refer to a type declared in <paramref name="declarer"/>: the agent
    /// and the app each see their own code and Core's (never each other's); tests see everything.
    /// </summary>
    private static bool CanUse(string user, string declarer) => Program(declarer) switch
    {
        "core" => true,
        var p => Program(user) is var u && (u == p || u == "tests"),
    };

    private static string Program(string file) =>
        file.StartsWith("src/Rigsight.Core/") ? "core" : file.StartsWith("src/Rigsight.Agent/") ? "agent" : file.StartsWith("src/Rigsight/") ? "app" : "tests";

    public IEnumerable<string> FilesMentioning(string text) => _text.Where(kv => kv.Value.Contains(text, StringComparison.Ordinal)).Select(kv => kv.Key);

    public IEnumerable<string> TestClassesIn(IEnumerable<string> files) =>
        files.SelectMany(f => _testClasses.GetValueOrDefault(f) ?? []);

    /// <summary>How far a changed file reaches: how many program files and test classes.</summary>
    public string ReachSummary(string file)
    {
        var reach = Affected([file]);
        int programs = reach.Count(f => f.StartsWith("src/")), tests = TestClassesIn(reach).Count();
        return $"{programs} program file(s), {tests} test class(es)";
    }
}
