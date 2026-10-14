namespace Reporting.Infrastructure.Domains.Finance;

/// <summary>
/// Where the Inventory Valuation reads its exchange rates — configuration section
/// "InventoryValuation". The rates themselves always come from CCURRA, never from code.
/// </summary>
public sealed class InventoryValuationOptions
{
    public const string SectionName = "InventoryValuation";

    /// <summary>CCURRA division (CUDIVI) holding the rates.</summary>
    public string RateDivision { get; init; } = "D";

    /// <summary>CCURRA rate type (CUCRTP) for daily spot rates — written by the RBA sync.</summary>
    public string SpotRateType { get; init; } = "99";

    /// <summary>CCURRA rate type (CUCRTP) for the Movex book rates that FCAAVP costs are stored at.</summary>
    public string BookRateType { get; init; } = "1";
}

/// <summary>A Movex book rate (foreign currency per 1 AUD) and the date it took effect.</summary>
public sealed record BookRate(string Currency, decimal Rate, DateOnly? EffectiveFrom);
