using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.ExchangeRate;

namespace Reporting.Api.Controllers;

/// <summary>
/// Health check endpoints — no authentication required.
/// Returns Degraded when catalog is serving last-known-good after reload failure (Gap 6).
/// Returns Degraded when exchange rate sync has failed.
///
/// Uses skill: architecture/dotnet-api-design v1.0
/// </summary>
[ApiController]
[Route("api/v1/health")]
[Produces("application/json")]
[DisableRateLimiting]
public sealed class HealthController : ControllerBase
{
    private readonly IReportCatalogProvider _catalog;
    private readonly ExchangeRateSyncService _exchangeRateSync;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IReportCatalogProvider catalog,
        ExchangeRateSyncService exchangeRateSync,
        ILogger<HealthController> logger)
    {
        _catalog = catalog;
        _exchangeRateSync = exchangeRateSync;
        _logger = logger;
    }

    /// <summary>
    /// Returns the overall service health status.
    /// Status: Healthy | Degraded | Unhealthy
    /// Degraded when catalog is serving last-known-good, or exchange rate sync has failed.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealth()
    {
        Response.Headers["X-Api-Version"] = "1.0";

        var syncStatus = _exchangeRateSync.LastStatus;
        var catalogDegraded = _catalog.IsServingLastKnownGood;
        var syncDegraded = syncStatus is SyncStatus.Degraded or SyncStatus.Failed;

        if (catalogDegraded || syncDegraded)
        {
            return Ok(new
            {
                status = "Degraded",
                reason = catalogDegraded
                    ? $"Catalog reload failed: {_catalog.LastReloadFailureReason}"
                    : $"Exchange rate sync failed: {_exchangeRateSync.LastSyncError}",
                catalog = new
                {
                    status = catalogDegraded ? "Degraded" : "Healthy",
                    isServingLastKnownGood = _catalog.IsServingLastKnownGood
                },
                exchangeRateSync = BuildSyncStatus(),
                timestamp = DateTime.UtcNow
            });
        }

        return Ok(new
        {
            status = "Healthy",
            catalog = new { status = "Healthy" },
            exchangeRateSync = BuildSyncStatus(),
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Checks connectivity to all configured data sources.
    /// Sprint 1: stub — data source probes wired in Sprint 2 with Db2DirectFetcher.
    /// </summary>
    [HttpGet("data-sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetDataSourceHealth()
    {
        Response.Headers["X-Api-Version"] = "1.0";

        // TODO T12: Wire real DB2 and SQL Server connectivity probes in Sprint 2
        return Ok(new
        {
            status = "Unknown",
            reason = "Data source probes not yet implemented (Sprint 2)",
            dataSources = new[]
            {
                new { name = "Db2Direct", status = "Unknown" },
                new { name = "SqlServerDw", status = "Unknown" }
            },
            timestamp = DateTime.UtcNow
        });
    }

    private object BuildSyncStatus() => new
    {
        status = _exchangeRateSync.LastStatus.ToString(),
        lastSyncUtc = _exchangeRateSync.LastSyncUtc,
        lastSyncError = _exchangeRateSync.LastSyncError,
        nextScheduledSyncUtc = _exchangeRateSync.NextScheduledSyncUtc,
        skipReason = _exchangeRateSync.SkipReason
    };
}
