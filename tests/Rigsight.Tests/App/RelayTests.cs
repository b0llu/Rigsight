using Rigsight.Core.Ai;

namespace Rigsight.Tests.App;

/// <summary>The app's side of RigSight AI: how it names this PC to the relay, and the relay's errors put in words.</summary>
public sealed class RelayTests
{
    [Fact]
    public void This_PC_is_a_hash_the_relay_accepts_and_the_same_every_time()
    {
        Assert.Matches("^[0-9a-f]{64}$", Relay.DeviceId);
        Assert.Equal(Relay.DeviceId, Relay.DeviceId);
    }

    [Theory]
    [InlineData("""{"error":"out_of_questions","reason":"free"}""", 402, "used its free questions")]
    [InlineData("""{"error":"out_of_questions"}""", 402, "out of questions")]
    [InlineData("""{"error":"licence","reason":"other_pc"}""", 403, "in use on another PC")]
    [InlineData("""{"error":"licence","reason":"refunded"}""", 403, "refunded")]
    [InlineData("""{"error":"licence","reason":"disabled"}""", 403, "turned off")]
    [InlineData("""{"error":"update_needed","min_version":"0.32.0"}""", 426, "latest RigSight")]
    [InlineData("""{"error":"unavailable","reason":"busy"}""", 503, "busy")]
    [InlineData("""{"error":"unavailable","reason":"model"}""", 503, "wasn't counted")]
    [InlineData("<html>bad gateway</html>", 502, "wasn't counted")]
    [InlineData("""{"error":"unavailable","reason":"stopped","detail":"too many rounds"}""", 503, "too many steps")]
    public void Each_kind_of_refusal_has_its_own_words(string body, int status, string says) =>
        Assert.Contains(says, Relay.Problem(body, status));

    [Fact]
    public void A_key_on_another_PC_says_one_licence_one_PC_when_activating()
    {
        string body = """{"error":"licence","reason":"other_pc"}""";
        Assert.Contains("A licence works on one PC", Relay.Problem(body, 403, activating: true));
        Assert.Contains("not_found", """{"error":"licence","reason":"not_found"}""");
        Assert.Contains("wasn't found", Relay.Problem("""{"error":"licence","reason":"not_found"}""", 403, activating: true));
    }

    // Shown in Settings › Ask RigSight beside Email support: "Contact support from Settings › Ask RigSight" pointed at the
    // page they were on (2026-10-22). The chat keeps the way there.
    [Fact]
    public void A_turned_off_licence_in_Settings_doesnt_send_them_to_Settings()
    {
        string body = """{"error":"licence","reason":"disabled"}""";
        Assert.DoesNotContain("Settings", Relay.Problem(body, 403, activating: true, inSettings: true));
        Assert.DoesNotContain("Settings", Relay.Problem(body, 403, inSettings: true));
        Assert.Contains("Settings › Ask RigSight", Relay.Problem(body, 403));
    }
}
