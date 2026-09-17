using Microsoft.AspNetCore.Mvc;
using Reporting.Api.Models;
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
    /// Rate convention: 1 AUD = {rate} {currency}  (e.g. 1 AUD = 0.7114 USD).
    /// Matches RBA F11.1 ("A$1=USD") and M3 CCURRA.CUARAT. Consumers must NOT invert.
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
    [ProducesResponseType(typeof(ExchangeRateQueryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
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
                "Exchange rate query: {Currency} on {Date} → 1 AUD = {Rate} {Currency} (fallback={Fallback})",
                currency, requestedDate, rateData.Rate, currency, rateData.UsedFallback);

            var result = new ExchangeRateQueryResult
            {
                Currency = rateData.Currency,
                RequestedDate = requestedDate.ToString("yyyy-MM-dd"),
                EffectiveDate = rateData.EffectiveDate.ToString("yyyy-MM-dd"),
                Rate = rateData.Rate,
                RateType = "SPOT",
                Source = "RBA",
                UsedFallback = rateData.UsedFallback,
                IsWeekend = isWeekend,
                LastSyncUtc = _syncService.LastSyncUtc,
                CorrelationId = HttpContext.TraceIdentifier,
                Timestamp = DateTime.UtcNow
            }; return Ok(result);
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

    /// <summary>
    /// Forces an immediate RBA sync, independently of the internal 23:00 UTC timer.
    ///
    /// Called daily at 23:30 UTC (09:30 AEST) by the `ReportingService-ExchangeRateSync`
    /// Windows Task Scheduler job on SRXWEBAPP1. This is a redundant safety net, not a
    /// replacement for the internal timer.
    ///
    /// Why it exists: the in-process timer only runs while the IIS worker is alive. A
    /// misconfigured app pool (startMode=OnDemand) left the worker stopped and the sync
    /// silently dead for four months in 2026. An external HTTP call does not depend on IIS
    /// keeping anything alive — the inbound request itself starts the worker, which then
    /// syncs. It therefore survives an app-pool config regression.
    ///
    /// Normal case: the internal timer already ran at 23:00, so this call is a no-op —
    /// Db2ExchangeRateWriter skips dates already present rather than overwriting.
    /// Honours SkipWeekends: a weekend call returns Skipped without contacting RBA.
    ///
    /// Authentication: X-API-Key header (ApiKeyMiddleware, applied globally).
    /// </summary>
    /// <response code="200">Sync ran. Inspect `status` for the outcome.</response>
    /// <response code="401">Missing or invalid X-API-Key.</response>
    /// <response code="503">Sync is disabled (ExchangeRateSync:Enabled=false), or it failed.</response>
    [HttpPost("sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> TriggerSync(CancellationToken ct)
    {
        var startedUtc = DateTime.UtcNow;
        var status = await _syncService.TriggerSyncAsync(ct).ConfigureAwait(false);

        // Distinguish "ran and did nothing because it's the weekend" from "ran and succeeded"
        var skipped = _syncService.SkipReason is not null;

        _logger.LogInformation(
            "Manual sync trigger completed: status={Status} skipped={Skipped} elapsed={ElapsedMs}ms",
            status, skipped, (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds);

        var body = new
        {
            status      = skipped ? "Skipped" : status.ToString(),
            skipReason  = _syncService.SkipReason,
            lastSyncUtc = _syncService.LastSyncUtc,
            lastError   = _syncService.LastSyncError,
            nextScheduledSyncUtc = _syncService.NextScheduledSyncUtc,
            triggeredAtUtc = startedUtc,
            elapsedMs   = (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds,
            correlationId = HttpContext.TraceIdentifier
        };

        // Non-200 so the scheduled task's failure check catches a disabled or broken sync.
        // Skipped (weekend) is a success — there is genuinely nothing to do.
        if (!skipped && status is SyncStatus.NotRun or SyncStatus.Failed)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, body);

        return Ok(body);
    }

    public class ErrorResponse
    {
        public required string Code { get; set; }
        public required string Message { get; set; }
        public required string CorrelationId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
