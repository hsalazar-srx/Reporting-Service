namespace Reporting.Infrastructure.ExchangeRate.Models;

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

/// <summary>
/// Query result returned by the portal API endpoint (api/v1/exchange-rates/{currency}/{date}).
/// Wraps ExchangeRateData with HTTP-response-friendly metadata.
/// </summary>
public sealed class ExchangeRateQueryResult
{
    public required string Currency { get; init; }
    public required string RequestedDate { get; init; }    // ISO 8601 date string
    public required string EffectiveDate { get; init; }    // May differ from RequestedDate on fallback
    public decimal Rate { get; init; }
    public string RateType { get; init; } = "SPOT";
    public string Source { get; init; } = "RBA";
    public bool UsedFallback { get; init; }
    public bool IsWeekend { get; init; }
    public DateTime? LastSyncUtc { get; init; }
    public required string CorrelationId { get; init; }
    public DateTime Timestamp { get; init; }
}
