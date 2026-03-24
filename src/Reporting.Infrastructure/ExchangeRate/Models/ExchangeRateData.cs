namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Represents a daily SPOT exchange rate fetched from RBA and stored in mvxcdta.CCURRA.
/// Rate convention: 1 {Currency} = {Rate} AUD  (e.g. 1 USD = 0.6828 AUD)
/// </summary>
public sealed class ExchangeRateData
{
    /// <summary>ISO 4217 currency code (e.g. "USD").</summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Exchange rate. Interpretation: 1 {Currency} = {Rate} AUD.
    /// Sourced from RBA Table F11.1 CSV.
    /// </summary>
    public decimal Rate { get; init; }

    /// <summary>The business date this rate is valid for (weekdays only).</summary>
    public DateOnly EffectiveDate { get; init; }

    /// <summary>UTC timestamp when RBA CSV was fetched.</summary>
    public DateTime FetchedAtUtc { get; init; }

    /// <summary>
    /// True when the rate comes from a prior weekday (Friday rollover for weekends/holidays).
    /// Always false for rates fetched from the sync service — only set when queried by date.
    /// </summary>
    public bool UsedFallback { get; init; }

    /// <summary>The actual date from which the rate was sourced when UsedFallback is true.</summary>
    public DateOnly? FallbackFromDate { get; init; }

    /// <summary>Source identifier, e.g. "RBA:F11.1".</summary>
    public string Source { get; init; } = "RBA:F11.1";
}
