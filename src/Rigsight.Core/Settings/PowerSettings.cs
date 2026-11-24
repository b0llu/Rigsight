namespace Rigsight.Core.Settings;

/// <summary>
/// The Power page's electricity price (1.18.0, approved mock-up 2026-11-23): one price a kWh for every hour, used for every
/// day shown, so the page can say an estimated cost. None until the person types one.
/// </summary>
public sealed class PowerSettings
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; set; }

    /// <summary>The price of a kWh in <see cref="Currency"/>; null: no price given.</summary>
    public double? PricePerKwh { get; set; }

    /// <summary>The price's currency as an ISO code ("INR"); null until a price is saved (it then starts as Windows' Country or region's, see <see cref="Money.HomeCurrency"/>).</summary>
    public string? Currency { get; set; }
}
