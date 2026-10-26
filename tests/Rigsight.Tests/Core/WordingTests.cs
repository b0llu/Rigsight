using System.Text;
using System.Text.RegularExpressions;
using Rigsight.Core;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Core;

/// <summary>
/// The words rule set (WORDING.md) held in place: the forms it retired must not come back in anything the app says,
/// and the shared words read the way the rule set says.
/// </summary>
public sealed class WordingTests
{
    /// <summary>Retired forms, each with what to say instead (the failure names both).</summary>
    private static readonly (Regex Form, string Say)[] Retired =
    [
        (Ci("Confirmed by Windows"), "a confirmed cause is plain fact, with no tag"),
        (Ci("Possible, not certain"), "\"possibly …\""),
        (Cs(@"\bPOSSIBLE:"), "POSSIBLY <CAUSE>"),
        (Ci("Not enough to say why"), "\"Not enough on record to say why.\""),
        (Ci("closed unexpectedly"), "\"crashed\" or \"froze\""),
        (Ci(@"stopped on an (unhandled )?error"), "\"crashed\""),
        (Cs(@"\bNOT RESPONDING\b"), "FROZE"),
        (Ci(@"unexpected shutdowns?\b"), "\"sudden shut-off\""),
        (Ci("shut off unexpectedly"), "\"shut off suddenly\""),
        (Ci(@"sudden shutdowns?\b"), "\"sudden shut-off\""),
        (Cs(@"\bPOWER LOST\b"), "\"likely a power cut\" (lost power only as a cause)"),
        (Ci(@"\bGPU driver"), "\"graphics driver\""),
        (Ci("Graphics trouble"), "\"graphics driver reset\""),
        (Ci("Click for the full breakdown"), Wording.FullPicture),
        (Ci("Click for the full recap"), Wording.FullPicture),
        (Ci(@"Nothing recorded"), "\"Not recorded\", \"No reading\" or \"No …\" naming what didn't happen"),
        (Ci(@"No readings recorded"), "\"Not recorded\" or \"No reading\""),
        (Ci(@"No temperatures recorded"), "\"Not recorded\" or \"No reading\""),
        (Ci(@"\bavg\b(?!\s*\()"), "Average"), // not SQL's avg()
        (Cs(@"\b(MIN|MAX)\b"), "Lowest / Highest"),
        (Cs(@"\bTypical\b"), "Average, worked out as an average"),
        (Ci(@"\bpeak (CPU|GPU)\b"), "\"CPU peak\", \"GPU peak\""),
        (Ci(@"\bHottest (CPU|GPU)\b"), "\"CPU peak\", \"GPU peak\""),
        (Ci("your daily average"), "\"your usual\""),
        (Ci("over the previous 7 days"), "\"the last 7 days\" or \"the 7 days before\""),
        (Ci(@"\bin the week before\b"), "\"in the 7 days before\""),
    ];

    /// <summary>
    /// What may still say a retired form, and why. Past What's new entries are history; Windows' own words in a crash's
    /// technical details are quoted as Windows wrote them (they are also what a web search finds).
    /// </summary>
    private static readonly string[] Allowed =
    [
        @"src\Rigsight\Services\Changelog.cs", // past What's new entries are not changed
        @"src\Rigsight.Core\Stability\CrashLogReader.cs", // Windows' own event text, quoted in a crash's technical details
    ];

    /// <summary>One form left in one file, with the reason beside it (none at present).</summary>
    private static readonly (string File, string Form)[] AllowedOnce =
    [
    ];

    /// <summary>
    /// Whole literals that are keys, not words anyone reads: the temperature graph's line setting is stored as "Avg",
    /// "High" or "Low" in settings.json, and the page shows Average, Highest and Lowest for them.
    /// </summary>
    private static readonly string[] Keys = ["Avg"];

    [Fact]
    public void No_retired_form_is_said_anywhere()
    {
        var found = new List<string>();
        foreach (var file in SourceFiles())
        {
            string rel = Path.GetRelativePath(Repo.Root, file);
            if (Allowed.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
            var said = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? XamlText(File.ReadAllText(file)) : CsLiterals(File.ReadAllText(file));
            foreach (var (line, text) in said)
                foreach (var (form, say) in Retired)
                    if (!Keys.Contains(text) && form.Match(text) is { Success: true } m
                        && !AllowedOnce.Any(a => a.File.Equals(rel, StringComparison.OrdinalIgnoreCase) && a.Form == m.Value))
                        found.Add($"{rel}:{line}: \"{m.Value}\" in \"{Clip(text)}\" (say {say})");
        }
        Assert.True(found.Count == 0, $"{found.Count} retired form(s) (WORDING.md):\n" + string.Join("\n", found));
    }

    [Fact]
    public void The_test_reads_what_the_app_says_and_skips_comments()
    {
        const string cs = """
            // Nothing recorded in a comment
            /// <summary>closed unexpectedly</summary>
            var a = "Not recorded"; /* MAX */ var b = $"{(x ? "AVG" : "b")} shown {y}";
            var c = @"verbatim ""Typical"" text"; char q = '"'; var d = "after the quote";
            """;
        var said = CsLiterals(cs).Select(s => s.Text).ToList();
        Assert.Contains("Not recorded", said);
        Assert.Contains("AVG", said);
        Assert.Contains(" shown ", said);
        Assert.Contains("verbatim \"Typical\" text", said);
        Assert.Contains("after the quote", said);
        Assert.DoesNotContain(said, s => s.Contains("Nothing recorded") || s.Contains("closed") || s.Contains("MAX"));

        var xaml = XamlText("""<StackPanel><!-- AVG here --><TextBlock Text="Highest" ToolTip="Lowest" /><Run>Average</Run><TextBlock Text="{Binding Max}" /></StackPanel>""")
            .Select(s => s.Text).ToList();
        Assert.Equal(["Highest", "Lowest", "Average", "{Binding Max}"], xaml);
    }

    [Fact]
    public void The_confidence_is_about_the_cause_and_the_event_is_a_fact()
    {
        Assert.Equal("CONFIRMED", Wording.Chip(Confidence.Named, null));
        Assert.Equal("LIKELY A POWER CUT", Wording.Chip(Confidence.Likely, "A POWER CUT"));
        Assert.Equal("POSSIBLY HEAT", Wording.Chip(Confidence.Possible, "HEAT"));
        Assert.Equal("NOT ENOUGH TO SAY", Wording.Chip(Confidence.NotEnough, "ANYTHING"));
        Assert.Equal("Likely a power cut", Wording.HowSure(Confidence.Likely, "A POWER CUT"));
        Assert.Equal("Possibly the GPU hung", Wording.HowSure(Confidence.Possible, "THE GPU HUNG"));

        // The two sentences the rule set was written from.
        var dotnet = new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "RigsightTestCrash.exe", Ts = 1, Facts = new CrashFacts { Error = "System.NullReferenceException" } };
        var a = CrashFindings.Judge(new CrashInput(dotnet, CrashExplainer.Explain(dotnet, "RigsightTestCrash"), new Rigsight.Core.Settings.AlertSettings()) { AppName = "RigsightTestCrash" });
        Assert.Equal("RigsightTestCrash crashed at 10:58 AM with a .NET error (NullReferenceException).", a.Told("10:58 AM"));

        var start = new CrashEvent { Kind = CrashKind.AppCrash, AppExe = "RocketLeague.exe", Module = "RocketLeague.exe", Ts = 1, Facts = new CrashFacts { RanSec = 4 } };
        var b = CrashFindings.Judge(new CrashInput(start, CrashExplainer.Explain(start, "Rocket League"), new Rigsight.Core.Settings.AlertSettings()) { AppName = "Rocket League", IsGame = true });
        Assert.Equal("Rocket League crashed at 11:07 PM, likely as it was starting.", b.Told("11:07 PM"));
        Assert.Equal("LIKELY AT START-UP", b.Chip);
    }

    [Theory]
    [InlineData(CrashKind.AppCrash, "Dota 2 crashed", "APP CRASH")]
    [InlineData(CrashKind.AppHang, "Dota 2 froze", "FROZE")]
    [InlineData(CrashKind.SystemCrash, "Blue screen", "BLUE SCREEN")]
    [InlineData(CrashKind.UnexpectedShutdown, "Sudden shut-off", "SUDDEN SHUT-OFF")]
    [InlineData(CrashKind.GpuDriverReset, "Graphics driver reset", "GRAPHICS DRIVER RESET")]
    public void Each_kind_has_one_name(CrashKind kind, string title, string chip)
    {
        Assert.Equal(title, Wording.Title(kind, "Dota 2"));
        Assert.Equal(chip, Wording.KindChip(kind));
    }

    // ── Reading what the app says ───────────────────────────────────────

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Every string literal in C# source with the line it starts on: the text parts of an interpolated one (its holes
    /// are code, read the same way), never a comment.
    /// </summary>
    internal static List<(int Line, string Text)> CsLiterals(string src)
    {
        var said = new List<(int, string)>();
        int i = 0, line = 1;
        Code(src, ref i, ref line, said, untilBrace: false);
        return said;
    }

    private static void Code(string s, ref int i, ref int line, List<(int, string)> said, bool untilBrace)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') line++; i++; }
                i += 2;
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'') i += s[i] == '\\' ? 2 : 1;
                i++;
                continue;
            }
            if (c is '$' or '@' or '"')
            {
                int j = i;
                bool interp = false, verbatim = false;
                while (j < s.Length && s[j] is '$' or '@') { interp |= s[j] == '$'; verbatim |= s[j] == '@'; j++; }
                if (j < s.Length && s[j] == '"')
                {
                    i = j;
                    String(s, ref i, ref line, said, interp, verbatim);
                    continue;
                }
            }
            if (untilBrace)
            {
                if (c == '{') depth++;
                else if (c == '}' && depth-- == 0) { i++; return; }
            }
            i++;
        }
    }

    private static void String(string s, ref int i, ref int line, List<(int, string)> said, bool interp, bool verbatim)
    {
        int quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
        int start = line;
        var text = new StringBuilder();
        if (quotes >= 3)
        {
            // A raw string: as it is, to the same run of quotes.
            i += quotes;
            string close = new('"', quotes);
            int end = s.IndexOf(close, i, StringComparison.Ordinal);
            if (end < 0) end = s.Length;
            foreach (var part in s[i..end].Split('\n')) { said.Add((start++, part.TrimEnd('\r'))); }
            line = start - 1;
            i = end + quotes;
            return;
        }
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { text.Append('"'); i += 2; continue; }
                i++;
                break;
            }
            if (c == '\\' && !verbatim) { if (i + 1 < s.Length) text.Append(s[i + 1]); i += 2; continue; }
            if (interp && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { text.Append('{'); i += 2; continue; }
                said.Add((start, text.ToString()));
                text.Clear();
                i++;
                Code(s, ref i, ref line, said, untilBrace: true);
                start = line;
                continue;
            }
            if (interp && c == '}' && i + 1 < s.Length && s[i + 1] == '}') { text.Append('}'); i += 2; continue; }
            if (c == '\n') line++;
            text.Append(c);
            i++;
        }
        said.Add((start, text.ToString()));
    }

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex XamlSaid = new(@"\b(Text|ToolTip|Content|Header|Title|Value|AutomationProperties\.Name|ctl:\w+\.\w+)=""([^""]*)""|>([^<>]+)<", RegexOptions.Compiled);

    /// <summary>What a XAML file shows: the text of its words-bearing attributes and of its elements, never a comment.</summary>
    internal static List<(int Line, string Text)> XamlText(string xaml)
    {
        // Comments blanked, not removed, so the lines still count true.
        string text = XamlComment.Replace(xaml, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
        var said = new List<(int, string)>();
        foreach (Match m in XamlSaid.Matches(text))
        {
            string value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(value)) continue;
            int line = 1 + text.AsSpan(0, m.Index).Count('\n');
            said.Add((line, System.Net.WebUtility.HtmlDecode(value.Trim())));
        }
        return said;
    }

    private static string Clip(string s) => s.Length <= 90 ? s : s[..87] + "...";

    private static Regex Ci(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex Cs(string pattern) => new(pattern, RegexOptions.Compiled);
}
