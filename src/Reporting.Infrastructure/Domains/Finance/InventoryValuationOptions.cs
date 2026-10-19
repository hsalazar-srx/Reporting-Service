namespace Reporting.Infrastructure.Domains.Finance;

/// <summary>
/// Where the Inventory Valuation reads its spot rates — configuration section "InventoryValuation".
/// The spot rates themselves come from CCURRA. Book rates are not here: they are report parameters,
/// defaulted in config/report-catalog.json and adjustable on the report page.
/// </summary>
public sealed class InventoryValuationOptions
{
    public const string SectionName = "InventoryValuation";

    /// <summary>CCURRA division (CUDIVI) holding the rates.</summary>
    public string RateDivision { get; init; } = "D";

    /// <summary>CCURRA rate type (CUCRTP) for daily spot rates — written by the RBA sync.</summary>
    public string SpotRateType { get; init; } = "99";
}

/// <summary>
/// A book rate (foreign currency per 1 AUD) used for a run: what was entered on the report page, and
/// the catalog default it replaced (equal when left alone).
/// </summary>
public sealed record BookRate(string Currency, decimal Rate, decimal DefaultRate)
{
    public bool IsDefault => Rate == DefaultRate;
}
