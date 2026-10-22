using System.Globalization;
using System.Text.Json;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>The clock times are written in: Windows' own, or 12-hour or 24-hour as picked, everywhere a time is shown.</summary>
public class TimeTextTests
{
    private static readonly DateTime Evening = new(2026, 10, 21, 18, 41, 7);

    [Theory]
    [InlineData("h:mm tt", "H:mm")]
    [InlineData("h:mm:ss tt", "H:mm:ss")]
    [InlineData("ddd d MMM, h:mm tt", "ddd d MMM, H:mm")]
    [InlineData("h tt", "H:00")]
    [InlineData("ddd h tt", "ddd H:00")]
    [InlineData("h:mm", "H:mm")]
    [InlineData("d MMM", "d MMM")]
    [InlineData("t", "H:mm")]
    public void A_24_hour_clock_turns_12_hour_formats_into_its_own(string format, string expected)
    {
        using var _ = TimeText.UseHere(ClockStyle.H24);
        Assert.Equal(expected, TimeText.Fit(format));
    }

    [Fact]
    public void A_12_hour_clock_keeps_the_formats_as_written()
    {
        using var _ = TimeText.UseHere(ClockStyle.H12);
        Assert.Equal("ddd d MMM, h:mm tt", TimeText.Fit("ddd d MMM, h:mm tt"));
        Assert.Equal("h:mm tt", TimeText.Fit("t"));
    }

    [Fact]
    public void The_same_evening_reads_18_41_or_6_41_PM()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        using (TimeText.UseHere(ClockStyle.H24)) Assert.Equal("18:41", TimeText.At(Evening, "h:mm tt", en));
        using (TimeText.UseHere(ClockStyle.H12)) Assert.Equal("6:41 PM", TimeText.At(Evening, "h:mm tt", en));
    }

    [Fact]
    public void Translated_and_English_texts_follow_the_clock_with_their_keys_unchanged()
    {
        var pack = LanguagePack.Read(JsonSerializer.Serialize(new
        {
            code = "es", name = "Español", version = 2, forApp = "0.30.0",
            strings = new Dictionary<string, string> { ["Tracking is paused until {0:h:mm tt}."] = "Seguimiento en pausa hasta las {0:h:mm tt}." },
        }));
        using var clock = TimeText.UseHere(ClockStyle.H24);
        using (Lang.UseHere(pack)) Assert.Equal("Seguimiento en pausa hasta las 18:41.", Lang.T($"Tracking is paused until {Evening:h:mm tt}."));
        using (Lang.UseHere(null)) Assert.Equal("Tracking is paused until 18:41.", Lang.T($"Tracking is paused until {Evening:h:mm tt}."));
    }
}
