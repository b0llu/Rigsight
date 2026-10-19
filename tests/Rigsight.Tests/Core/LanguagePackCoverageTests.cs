using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rigsight.Core;
using Rigsight.LangTool;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// Every language pack in the repo covers every text the app shows. The English is the key, so new or reworded English
/// is untranslated until the pack catches up: this fails until it does, so a release never ships half in English.
/// </summary>
public sealed class LanguagePackCoverageTests
{
    private static readonly Lazy<TextScanner.Result> Scan = new(() => TextScanner.Scan(Path.Combine(Repo.Root, "src")));

    public static TheoryData<string> Packs()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo.Root, "languages"), "*.json"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void A_pack_translates_every_text_the_app_shows(string file)
    {
        var pack = LanguagePack.Load(Path.Combine(Repo.Root, "languages", file));
        var missing = Scan.Value.Texts.Keys.Where(k => !pack.Strings.ContainsKey(k)).ToList();
        Assert.True(pack.Dropped.Count == 0,
            $"{file}: {pack.Dropped.Count} entries don't fill the same holes as their English, e.g. \"{pack.Dropped.FirstOrDefault()}\"");
        Assert.True(missing.Count == 0,
            $"{file}: {missing.Count} texts not translated (run the language tool's sync, then translate), e.g. \"{missing.FirstOrDefault()}\"");
    }

    [Theory]
    [MemberData(nameof(Packs))]
    public void A_pack_is_named_for_its_language(string file)
    {
        var pack = LanguagePack.Load(Path.Combine(Repo.Root, "languages", file));
        Assert.Equal(Path.GetFileNameWithoutExtension(file), pack.Code);
        Assert.Contains(LanguagePack.Known, l => l.Code == pack.Code);
    }

    [Fact]
    public void The_scanner_reads_a_text_the_way_the_app_looks_it_up()
    {
        var result = new TextScanner.Result();
        TextScanner.ScanCode("""
            class C { void M(string app, double t, int n) {
                _ = Lang.T("Crashes");
                _ = Lang.T($"{app} crashed at {t:0.0}° {{x}}");
                _ = Core.Lang.T($"[{app,-8}]");
                _ = Lang.F("{0} froze", app);
                _ = Lang.Key("Average");
                _ = Lang.T(app);
                _ = Lang.C("connection", "Up");
                _ = Lang.C("time", $"At {t:t}");
            } }
            """, "C.cs", result);
        TextScanner.ScanXaml("""<TextBlock Text="{ctl:T Memory}" ToolTip="{ctl:T 'Average, today'}" Tag="{ctl:T 'It\'s &amp; more'}" />""", "V.xaml", result);
        Assert.Equal(["Crashes", "{0} crashed at {1:0.0}° {{x}}", "[{0,-8}]", "{0} froze", "Average", "‹connection›Up", "‹time›At {0:t}", "Memory", "Average, today", "It's & more"], result.Texts.Keys);
        Assert.Single(result.NotWritten);
    }

    [Fact]
    public void The_scanner_and_the_app_build_the_same_template()
    {
        // The app's side: the template LangText builds is what a pack holding exactly that key gets asked for.
        string app = "Dota 2";
        double t = 61.5;
        var pack = LanguagePack.Read("""{ "code": "es", "strings": { "{0} crashed at {1:0.0}° {{x}}": "ok {0} {1:0.0} {{y}}" } }""");
        using var _ = Lang.UseHere(pack);
        Assert.Equal(Lang.F("ok {0} {1:0.0} {{y}}", app, t), Lang.T($"{app} crashed at {t:0.0}° {{x}}"));
        Assert.StartsWith("ok Dota 2 ", Lang.T($"{app} crashed at {t:0.0}° {{x}}"));

        // The scanner's side, from the same source text.
        var call = CSharpSyntaxTree.ParseText("""_ = Lang.T($"{app} crashed at {t:0.0}° {{x}}");""").GetRoot()
            .DescendantNodes().OfType<InterpolatedStringExpressionSyntax>().Single();
        Assert.Equal("{0} crashed at {1:0.0}° {{x}}", TextScanner.Template(call));
    }
}
