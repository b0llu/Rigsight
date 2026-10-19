using System.IO;
using System.Text.Json;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// The spreadsheet export in the language in use: headings, file names, the read-me and the cells' words. In English
/// the files are exactly what they always were (people's sheets read them).
/// </summary>
[Collection("UI")]
public sealed class ExportLanguageTests
{
    private static readonly (DateTime From, DateTime To) Period = (DateTime.Today.AddDays(-400), DateTime.Today.AddDays(1));

    private static LanguagePack Spanish() => LanguagePack.Read(JsonSerializer.Serialize(new
    {
        code = "es",
        strings = new Dictionary<string, string>
        {
            ["Start"] = "Inicio", ["End"] = "Fin", ["App"] = "Aplicación", ["Program"] = "Programa", ["In use (min)"] = "En uso (min)",
            ["Game"] = "Juego", ["CPU peak (°C)"] = "Pico de CPU (°C)", ["GPU peak (°C)"] = "Pico de GPU (°C)",
            ["Yes"] = "Sí", ["No"] = "No", ["Sessions.csv"] = "Sesiones.csv", ["App crash"] = "Fallo de aplicación",
            ["settings.json holds the settings."] = "settings.json guarda los ajustes.",
        },
    }));

    private static string Sessions()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        using var writer = new StringWriter();
        DataExport.Write(db, ExportTable.Sessions, Period.From, Period.To, SeedData.QuietSettings(), writer);
        return writer.ToString();
    }

    [Fact]
    public void In_English_the_files_are_as_they_always_were()
    {
        using var _ = Lang.UseHere(null);
        var lines = Sessions().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Start,End,App,Program,In use (min),Game,CPU peak (°C),GPU peak (°C)", lines[0]);
        Assert.Contains(lines.Skip(1), l => l.Contains(",Yes,") || l.Contains(",No,"));
        Assert.DoesNotContain(lines.Skip(1), l => l.Contains(",Sí,"));
        Assert.Equal("Sessions.csv", DataExport.FileName(ExportTable.Sessions));

        string readMe = DataExport.ReadMe(new DateTime(2026, 10, 10, 18, 5, 0), "9.9.9");
        string nl = Environment.NewLine;
        Assert.StartsWith("RigSight full copy, made 10 October 2026, 18:05 by RigSight 9.9.9." + nl + nl
            + "rigsight.db is the history itself, a SQLite database: everything RigSight has recorded on this PC. Putting it" + nl
            + @"back in %LocalAppData%\Rigsight (with RigSight closed from its tray icon) restores the history on this or another PC." + nl
            + "settings.json holds the settings." + nl + nl
            + "The other files are the same history as tables (CSV: comma-separated, UTF-8), which Excel and most other" + nl
            + "programs open. Times are this PC's local time. Temperatures are in °C. An empty cell means nothing was recorded." + nl, readMe);
        Assert.Contains("  Start: When it started" + nl, readMe);
    }

    [Fact]
    public void In_another_language_the_headings_names_and_words_follow_it()
    {
        using var _ = Lang.UseHere(Spanish());
        var lines = Sessions().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Inicio,Fin,Aplicación,Programa,En uso (min),Juego,Pico de CPU (°C),Pico de GPU (°C)", lines[0]);
        Assert.Contains(lines.Skip(1), l => l.Contains(",Sí,") || l.Contains(",No,"));
        Assert.DoesNotContain(lines.Skip(1), l => l.Contains(",Yes,"));
        // Dates stay one way in every language: they're data.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2},", lines[1]);
        Assert.Equal("Sesiones.csv", DataExport.FileName(ExportTable.Sessions));
        Assert.Contains("Inicio", DataExport.Columns(ExportTable.Sessions).Select(c => c.Name));
        Assert.Contains("settings.json guarda los ajustes.", DataExport.ReadMe(new DateTime(2026, 10, 10, 18, 5, 0), "9.9.9"));
    }
}
