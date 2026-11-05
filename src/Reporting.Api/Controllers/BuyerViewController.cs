using Microsoft.AspNetCore.Mvc;
using Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

namespace Reporting.Api.Controllers;

/// <summary>
/// Supply Chain buyer view: the daily sync of the legacy MSP report into the buyer view store.
/// Pages and the edit API come later; for now this only starts and reports the sync.
///
/// Authentication: X-API-Key header (ApiKeyMiddleware, applied globally).
/// </summary>
[ApiController]
[Route("api/v1/supplychain/buyer-view")]
[Produces("application/json")]
public sealed class BuyerViewController(BuyerViewSyncService syncService, ILogger<BuyerViewController> logger)
    : ControllerBase
{
    /// <summary>
    /// Runs the sync now. Called by a Windows scheduled task on SRXWEBAPP1 after MSP's 05:30 run,
    /// as the safety net for the in-process timer. A file already applied is skipped, so calling
    /// it again is harmless.
    /// </summary>
    /// <response code="200">Sync ran (or another run was in progress). Inspect `state` and `results`.</response>
    /// <response code="401">Missing or invalid X-API-Key.</response>
    /// <response code="503">Sync is disabled (BuyerViewSync:Enabled=false), or a division failed.</response>
    [HttpPost("sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var status = await syncService.RunNowAsync(ct).ConfigureAwait(false);
        logger.LogInformation("[BuyerViewController] Sync requested: {State}", status.State);
        return Respond(status);
    }

    /// <summary>The last sync's outcome, without running one.</summary>
    [HttpGet("sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Status() => Ok(Body(syncService.LastStatus));

    private IActionResult Respond(BuyerViewSyncStatus status) =>
        // Non-200 so the scheduled task's failure check catches a disabled or broken sync.
        status.State is BuyerViewSyncState.Disabled or BuyerViewSyncState.Failed
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, Body(status))
            : Ok(Body(status));

    private object Body(BuyerViewSyncStatus status) => new
    {
        state = status.State.ToString(),
        lastRunUtc = status.LastRunUtc,
        error = status.Error,
        results = status.Results.Select(r => new
        {
            division = r.Division,
            file = r.FileName,
            result = r.Result,
            message = r.Message,
            rowsRead = r.RowsRead,
            linesNew = r.LinesNew,
            linesUpdated = r.LinesUpdated,
            linesClosed = r.LinesClosed,
            linesReopened = r.LinesReopened,
        }),
        correlationId = HttpContext.TraceIdentifier,
    };
}
