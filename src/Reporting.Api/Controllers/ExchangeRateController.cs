using Microsoft.AspNetCore.Mvc;
using Reporting.Infrastructure.ExchangeRate;

namespace Reporting.Api.Controllers;

/// <summary>
/// Query SPOT exchange rates from mvxcdta.CCURRA.
/// Used by SM-Portal to display rates for specific dates.
///
/// Authentication: X-API-Key header (via ApiKeyMiddleware — applied globally).
/// Weekend handling: Falls back to the most recent prior-weekday rate
///                   when querying Saturday, Sunday, or public holidays.
/// </summary>
[ApiController]
[Route("api/v1/exchange-rates")]
[Produces("application/json")]
public sealed class ExchangeRateController : ControllerBase
{
    private readonly Db2ExchangeRateReader _reader;
    private readonly ExchangeRateSyncService _syncService;
    private readonly ILogger<ExchangeRateController> _logger;

    public ExchangeRateController(
        Db2ExchangeRateReader reader,
        ExchangeRateSyncService syncService,
        ILogger<ExchangeRateController> logger)
    {
        _reader = reader;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the SPOT exchange rate for a given currency and date.
    /// Rate convention: 1 {currency} = {rate} AUD  (e.g. 1 USD = 0.6828 AUD).
    /// When no rate exists for the requested date (weekend/holiday),
    /// returns the most recent prior weekday rate with usedFallback=true.
    /// </summary>
    /// <param name="currency">ISO 4217 currency code, e.g. "USD".</param>
    /// <param name="date">Date in yyyy-MM-dd format, e.g. "2026-03-19".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Rate found (possibly via fallback).</response>
    /// <response code="400">Invalid currency code or date format.</response>
    /// <response code="404">No rate available within the fallback window.</response>
    [HttpGet("{currency}/{date}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRate(
        [FromRoute] string currency,
        [FromRoute] string date,
        CancellationToken ct)
    {
        currency = currency.ToUpperInvariant().Trim();

        if (!System.Text.RegularExpressions.Regex.IsMatch(currency, @"^[A-Z]{3}$"))
        {
            return BadRequest(new
            {
                code = "INVALID_CURRENCY",
                message = $"Currency code must be 3 uppercase letters (e.g. USD). Got: {currency}",
                correlationId = HttpContext.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
        }

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var requestedDate))
        {
            return BadRequest(new
            {
                code = "INVALID_DATE",
                message = $"Date must be in yyyy-MM-dd format (e.g. 2026-03-19). Got: {date}",
                correlationId = HttpContext.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
        }

        try
        {
            var rateData = await _reader.GetRateForDateAsync(currency, requestedDate, ct)
                .ConfigureAwait(false);

            var isWeekend = requestedDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            _logger.LogInformation(
                "Exchange rate query: {Currency} on {Date} → {Rate} AUD (fallback={Fallback})",
                currency, requestedDate, rateData.Rate, rateData.UsedFallback);

            return Ok(new
            {
                currency = rateData.Currency,
                requestedDate = requestedDate.ToString("yyyy-MM-dd"),
                effectiveDate = rateData.EffectiveDate.ToString("yyyy-MM-dd"),
                rate = rateData.Rate,
                rateType = "SPOT",
                source = "RBA",
                usedFallback = rateData.UsedFallback,
                isWeekend,
                lastSyncUtc = _syncService.LastSyncUtc,
                correlationId = HttpContext.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
        }
        catch (ExchangeRateNotFoundException ex)
        {
            _logger.LogWarning("No rate found: {Message}", ex.Message);

            return NotFound(new
            {
                code = "RATE_NOT_FOUND",
                message = ex.Message,
                correlationId = HttpContext.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
        }
    }
}
