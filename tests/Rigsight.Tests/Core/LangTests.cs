using System.Globalization;
using System.Text.Json;
using Rigsight.Core;

namespace Rigsight.Tests.Core;

/// <summary>The app's words in another language: lookups, filling, what a pack may hold, and English when it can't.</summary>
public class LangTests
{
    private static LanguagePack Pack(params (string English, string Text)[] strings) =>
        LanguagePack.Read(JsonSerializer.Serialize(new
        {
            code = "es",
            name = "Español",
            version = 2,
            forApp = "0.30.0",
            strings = strings.ToDictionary(s => s.English, s => s.Text),
        }));

    [Fact]
    public void English_comes_out_exactly_as_the_plain_string_would()
    {
        using var _ = Lang.UseHere(null);
        string app = "Dota 2";
        double temp = 61.46;
        Assert.Equal("Crashes", Lang.T("Crashes"));
        Assert.Equal($"{app} crashed at {temp:0.0}° {{really}}", Lang.T($"{app} crashed at {temp:0.0}° {{really}}"));
        Assert.Equal($"[{app,8}]", Lang.T($"[{app,8}]"));
    }

    [Fact]
    public void A_text_is_looked_up_whole_and_its_holes_filled_in_the_packs_order()
    {
        var pack = Pack(("Crashes", "Fallos"), ("{0} crashed at {1:0.0}°", "A {1:0.0}° se cerró {0}"));
        using var _ = Lang.UseHere(pack);
        string app = "Dota 2";
        double temp = 61.46;
        Assert.Equal("Fallos", Lang.T("Crashes"));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, "A {0:0.0}° se cerró Dota 2", temp), Lang.T($"{app} crashed at {temp:0.0}°"));
        Assert.Equal("es", Lang.Code);
    }

    [Fact]
    public void What_the_pack_lacks_stays_English()
    {
        using var _ = Lang.UseHere(Pack(("Crashes", "Fallos")));
        int n = 3;
        Assert.Equal("Memory", Lang.T("Memory"));
        Assert.Equal("3 crashes", Lang.T($"{n} crashes"));
    }

    [Fact]
    public void Braces_in_the_words_are_kept_apart_from_the_holes()
    {
        using var _ = Lang.UseHere(Pack(("{0} in {{brackets}}", "{0} entre {{llaves}}")));
        string x = "A";
        Assert.Equal("A entre {llaves}", Lang.T($"{x} in {{brackets}}"));
    }

    [Theory]
    [InlineData("{0} crashed", "{0} se cerró", true)]
    [InlineData("{0} crashed", "se cerró", false)]                 // a value lost
    [InlineData("{0} crashed", "{0} se cerró {1}", false)]          // a value that isn't there
    [InlineData("{0} at {1}", "a las {1}, {0} y {0}", true)]        // reordered and repeated
    [InlineData("{0} crashed", "{0} se cerró {", false)]            // broken braces
    [InlineData("Crashes", "Fallos {0}", false)]
    [InlineData("Crashes", "Fallos", true)]
    [InlineData("{0:0.0}°", "{0:0,0}°", true)]                      // its own format is its business
    public void An_entry_is_kept_only_when_it_fills_the_same_holes(string english, string text, bool fits)
    {
        Assert.Equal(fits, LanguagePack.Fits(english, text));
        var pack = Pack((english, text));
        Assert.Equal(fits, pack.Strings.ContainsKey(english));
        Assert.Equal(!fits, pack.Dropped.Contains(english));
    }

    [Fact]
    public void An_empty_translation_is_not_translated_yet()
    {
        var pack = Pack(("Crashes", ""));
        Assert.Empty(pack.Strings);
        Assert.Empty(pack.Dropped);
    }

    [Fact]
    public void A_pack_says_what_it_is()
    {
        var pack = Pack(("Crashes", "Fallos"));
        Assert.Equal(("es", "Español", 2, "0.30.0"), (pack.Code, pack.Name, pack.Version, pack.ForApp));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "name": "x", "strings": {} }""")]
    [InlineData("""{ "code": "en", "strings": {} }""")]                  // English is built in
    [InlineData("""{ "code": "../../x", "strings": {} }""")]
    [InlineData("""{ "code": "es" }""")]
    public void A_file_that_isnt_a_pack_is_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => LanguagePack.Read(json));

    [Fact]
    public void A_template_held_as_data_is_filled_the_same_way()
    {
        using var _ = Lang.UseHere(Pack(("{0} froze", "{0} se congeló")));
        Assert.Equal("Chrome se congeló", Lang.F("{0} froze", "Chrome"));
        Assert.Equal("Chrome crashed", Lang.F("{0} crashed", "Chrome"));
    }

    [Fact]
    public void One_flow_of_work_in_Spanish_leaves_the_others_English()
    {
        var pack = Pack(("Crashes", "Fallos"));
        string? other = null;
        using (Lang.UseHere(pack))
        {
            // A thread started as another test would be: without this one's context.
            var t = new Thread(() => other = Lang.T("Crashes"));
            using (ExecutionContext.SuppressFlow()) t.Start();
            t.Join();
            Assert.Equal("Fallos", Lang.T("Crashes"));
        }
        Assert.Equal("Crashes", other);
    }

    [Fact]
    public void The_release_asset_is_found_with_its_checksum_only()
    {
        using var doc = JsonDocument.Parse("""
            { "assets": [
                { "name": "Rigsight-Setup-0.30.0.exe", "size": 1, "browser_download_url": "a", "digest": "sha256:AA" },
                { "name": "Rigsight-Language-es.json", "size": 42, "browser_download_url": "https://x/es.json", "digest": "sha256:ABCD" },
                { "name": "Rigsight-Language-fr.json", "size": 42, "browser_download_url": "https://x/fr.json" }
            ] }
            """);
        Assert.Equal(("https://x/es.json", 42L, "abcd"), LanguageStore.FindAsset(doc.RootElement, LanguageStore.AssetName("es")));
        Assert.Null(LanguageStore.FindAsset(doc.RootElement, LanguageStore.AssetName("fr")));
        Assert.Null(LanguageStore.FindAsset(doc.RootElement, LanguageStore.AssetName("de")));
    }

    [Fact]
    public void The_same_English_with_another_meaning_has_its_own_entry()
    {
        string t = "9:14 PM";
        using (Lang.UseHere(null))
        {
            Assert.Equal("Up", Lang.C("connection", "Up"));
            Assert.Equal("At 9:14 PM", Lang.C("time", $"At {t}"));
        }
        using (Lang.UseHere(Pack(("Up", "Subida"), ("‹connection›Up", "Conectado"), ("At {0}", "A {0}"), ("‹time›At {0}", "A las {0}"))))
        {
            Assert.Equal("Subida", Lang.T("Up"));
            Assert.Equal("Conectado", Lang.C("connection", "Up"));
            Assert.Equal("A las 9:14 PM", Lang.C("time", $"At {t}"));
            Assert.Equal("A 9:14 PM", Lang.T($"At {t}"));
        }
        // Without its own entry, it's translated like the plain text.
        using (Lang.UseHere(Pack(("Up", "Subida"), ("At {0}", "A {0}"))))
        {
            Assert.Equal("Subida", Lang.C("connection", "Up"));
            Assert.Equal("A 9:14 PM", Lang.C("time", $"At {t}"));
        }
    }

    [Fact]
    public void Dates_take_the_packs_day_and_month_names_and_keep_the_regions_numbers()
    {
        var pack = LanguagePack.Read("""{ "code": "es", "monthPrefix": "de ", "strings": {} }""");
        var region = CultureInfo.GetCultureInfo("en-US");
        var culture = LanguageStore.NamesFor(pack, region);
        var day = new DateTime(2026, 10, 19, 21, 14, 0);
        Assert.Equal("lunes, 19 de octubre", day.ToString("dddd, d MMMM", culture));
        Assert.Equal("octubre 2026", day.ToString("MMMM yyyy", culture));
        Assert.Equal("lun 19 oct", day.ToString("ddd d MMM", culture).Replace(".", ""));
        Assert.Equal("9:14 PM", day.ToString("h:mm tt", culture));      // the region's clock
        Assert.Equal("1,234.5", 1234.5.ToString("N1", culture));        // and its numbers
        // A pack without a prefix: plain month names.
        var plain = LanguageStore.NamesFor(LanguagePack.Read("""{ "code": "es", "strings": {} }"""), region);
        Assert.Equal("lunes, 19 octubre", day.ToString("dddd, d MMMM", plain));
    }
}
