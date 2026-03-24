namespace Reporting.Api.Models;

/// <summary>
/// Represents the result of an exchange rate query, including metadata suitable for consumers.
/// Wraps <see cref="Reporting.Infrastructure.ExchangeRate.ExchangeRateData"/> with additional contextual information.
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
