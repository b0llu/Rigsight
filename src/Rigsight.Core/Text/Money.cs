using System.Globalization;
using System.Runtime.InteropServices;

namespace Rigsight.Core;

/// <summary>
/// Money for the Power page's estimated cost (1.18.0). The currency starts as the one of Windows' Country or region (the
/// home location), not the one of the number format: a PC set to English (United States) formats in India still pays in
/// rupees (the user's own PC, 2026-11-23). The amount itself is written in the number format in use.
/// </summary>
public static class Money
{
    /// <summary>The ISO code of the home location's currency ("INR"); the number format's, failing that; "USD" last.</summary>
    public static string HomeCurrency()
    {
        try
        {
            int geo = GetUserGeoID(GeoClassNation);
            var iso2 = new char[8];
            if (geo != GeoIdNotAvailable && GetGeoInfoW(geo, GeoIso2, iso2, iso2.Length, 0) > 1)
                return new RegionInfo(new string(iso2).TrimEnd('\0')).ISOCurrencySymbol;
        }
        catch (Exception ex) { Log.Write("power", $"Reading the home location's currency (expected Windows' Country or region), got {ex.GetType().Name}: {ex.Message}"); }
        try { return RegionInfo.CurrentRegion.ISOCurrencySymbol; } catch { return "USD"; }
    }

    private static readonly Lazy<Dictionary<string, (string Symbol, string Name)>> All = new(() =>
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var r = new RegionInfo(c.Name);
                if (!map.ContainsKey(r.ISOCurrencySymbol)) map[r.ISOCurrencySymbol] = (r.CurrencySymbol, r.CurrencyEnglishName);
            }
            catch { }
        }
        return map;
    });

    /// <summary>Every currency Windows knows: its code, symbol and name ("INR", "₹", "Indian Rupee"), by code.</summary>
    public static IReadOnlyList<(string Code, string Symbol, string Name)> Currencies() =>
        [.. All.Value.Select(p => (p.Key, p.Value.Symbol, p.Value.Name)).OrderBy(c => c.Key, StringComparer.Ordinal)];

    public static string Symbol(string code) => All.Value.TryGetValue(code, out var c) ? c.Symbol : code;

    /// <summary>
    /// A currency as the price box shows it closed, short enough for every currency to fit (the user, 2026-11-23: "₹ INR,
    /// India" was cut): "₹ INR", "R$ BRL", "F CFA XOF"; just the code where the symbol is the code itself or is written
    /// right to left ("د.إ.‏" would turn the line around), "AED".
    /// </summary>
    public static string Short(string code)
    {
        string symbol = Symbol(code).Replace("‏", "").Replace("‎", "").Trim();
        bool rtl = symbol.Any(ch => ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ');
        return symbol.Length == 0 || rtl || string.Equals(symbol, code, StringComparison.OrdinalIgnoreCase) ? code : $"{symbol} {code}";
    }

    /// <summary>
    /// An amount in a currency, in the number format in use: "₹183", "₹8.50", "$1,234.50"; whole from 100 (an estimate
    /// needs no paise), with what the price itself had below that.
    /// </summary>
    public static string Format(double amount, string code)
    {
        var nf = (NumberFormatInfo)CultureInfo.CurrentCulture.NumberFormat.Clone();
        nf.CurrencySymbol = Symbol(code);
        nf.CurrencyDecimalDigits = amount >= 100 || amount == Math.Round(amount) ? 0 : 2;
        return amount.ToString("C", nf);
    }

    private const int GeoClassNation = 16, GeoIso2 = 4, GeoIdNotAvailable = -1;

    [DllImport("kernel32.dll")]
    private static extern int GetUserGeoID(int geoClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetGeoInfoW(int location, int geoType, char[] data, int length, int langId);
}
