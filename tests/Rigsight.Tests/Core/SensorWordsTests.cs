using System.Text.Json;
using Rigsight.Core;
using Rigsight.LangTool;

namespace Rigsight.Tests.Core;

/// <summary>A sensor's own name in the language in use, for showing only; unknown names and English stay as they are.</summary>
public sealed class SensorWordsTests
{
    private static LanguagePack Pack(params (string English, string Text)[] strings) =>
        LanguagePack.Read(JsonSerializer.Serialize(new { code = "es", strings = strings.ToDictionary(s => s.English, s => s.Text) }));

    [Theory]
    [InlineData("GPU Hot Spot", "Punto caliente de la GPU")]
    [InlineData("Fan #2", "Ventilador n.º 2")]
    [InlineData("CPU Core #12", "Núcleo de CPU n.º 12")]
    [InlineData("Core #3 (SMU)", "Núcleo n.º 3 (SMU)")]
    [InlineData("CPU Core #1 Thread #2", "Núcleo de CPU n.º 1, hilo n.º 2")]
    [InlineData("GPU Fan 1", "Ventilador de GPU 1")]
    public void A_known_name_is_said_in_the_language_in_use(string name, string spanish)
    {
        using var _ = Lang.UseHere(Pack(
            ("‹sensor›GPU Hot Spot", "Punto caliente de la GPU"), ("‹sensor›Fan #{0}", "Ventilador n.º {0}"),
            ("‹sensor›CPU Core #{0}", "Núcleo de CPU n.º {0}"), ("‹sensor›Core #{0} (SMU)", "Núcleo n.º {0} (SMU)"),
            ("‹sensor›CPU Core #{0} Thread #{1}", "Núcleo de CPU n.º {0}, hilo n.º {1}"), ("‹sensor›GPU Fan {0}", "Ventilador de GPU {0}")));
        Assert.Equal(spanish, SensorWords.Show(name));
    }

    [Fact]
    public void A_sensor_word_can_differ_from_the_same_word_elsewhere()
    {
        using var _ = Lang.UseHere(Pack(("Memory", "Memoria (página)"), ("‹sensor›Memory", "Memoria")));
        Assert.Equal("Memoria", SensorWords.Show("Memory"));
        Assert.Equal("Memoria (página)", Lang.T("Memory"));
    }

    [Theory]
    [InlineData("GPU Hot Spot")]
    [InlineData("Fan #2")]
    [InlineData("Something new #4")]
    [InlineData("")]
    public void In_English_every_name_comes_back_as_it_is(string name)
    {
        using var _ = Lang.UseHere(null);
        Assert.Equal(name, SensorWords.Show(name));
    }

    [Fact]
    public void An_unknown_name_stays_as_it_is()
    {
        using var _ = Lang.UseHere(Pack(("‹sensor›Fan #{0}", "Ventilador n.º {0}")));
        Assert.Equal("Some Board Thing #3", SensorWords.Show("Some Board Thing #3"));
        Assert.Equal("D3D 3D", SensorWords.Show("D3D 3D"));
    }

    /// <summary>Names from the hardware library left as they are on purpose: engine and chip names, product names.</summary>
    private static readonly HashSet<string> LeftAsTheyAre =
    [
        "D3D 3D", "D3D Overlay", "D3D Security", "D3D Security_1", "D3D VR", "D3D Optical Flow Accelerator 0",
        "SoC (SVI2 TFN)", "CCD1 (Tdie)",
    ];

    [Fact]
    public void Every_sensor_name_on_a_real_PC_is_known_or_left_on_purpose()
    {
        // A pack that marks every sensor text, so a name is "known" when it comes back changed.
        var keys = TextScanner.Scan(Path.Combine(Repo.Root, "src")).Texts.Keys.Where(k => k.StartsWith("‹sensor›", StringComparison.Ordinal));
        using var _ = Lang.UseHere(Pack([.. keys.Select(k => (k, "§" + k))]));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.Root, "tests", "Rigsight.Tests", "Fixtures", "hello-ryzen-rtx.json")));
        var unknown = new List<string>();
        foreach (var hw in doc.RootElement.GetProperty("Hardware").EnumerateArray())
            foreach (var s in hw.GetProperty("Sensors").EnumerateArray())
            {
                string name = s.GetProperty("Name").GetString()!;
                if (SensorWords.Show(name) == name && !LeftAsTheyAre.Contains(name)) unknown.Add(name);
            }
        Assert.True(unknown.Count == 0, "Sensor names nobody decided on: " + string.Join(", ", unknown.Distinct()));
    }
}
