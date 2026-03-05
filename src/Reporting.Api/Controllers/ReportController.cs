using Microsoft.AspNetCore.Mvc;
using Reporting.Api.Models;
using Reporting.Core.Catalog;

namespace Reporting.Api.Controllers;

/// <summary>
/// Report catalog browsing and execution endpoints.
/// All routes versioned under /api/v1/ (Gap 13 mitigation).
///
/// Uses skill: data/reporting-integration v1.0
/// Uses skill: architecture/dotnet-api-design v1.0
/// </summary>
[ApiController]
[Route("api/v1/reports")]
[Produces("application/json")]
public sealed class ReportController : ControllerBase
{
    private readonly IReportCatalogProvider _catalog;
    private readonly ILogger<ReportController> _logger;

    public ReportController(IReportCatalogProvider catalog, ILogger<ReportController> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>
    /// Lists all reports, optionally filtered by domain.
    /// </summary>
    /// <param name="domain">
    /// Optional domain filter: CostManagement | SupplyChainPerformance |
    /// Finance | InventoryManagement | Procurement | Production
    /// </param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReportDefinition>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? domain,
        CancellationToken cancellationToken)
    {
        try
        {
            var reports = string.IsNullOrWhiteSpace(domain)
                ? await _catalog.GetAllAsync(cancellationToken)
                : await _catalog.GetByDomainAsync(domain, cancellationToken);

            Response.Headers["X-Api-Version"] = "1.0";
            return Ok(reports);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve catalog");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse("DATA_SOURCE_UNAVAILABLE", "Catalog unavailable.",
                    HttpContext.TraceIdentifier, DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Gets a single report definition by ID.
    /// </summary>
    /// <param name="id">Report ID (e.g., cost.average-cost-snapshot)</param>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ReportDefinition), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id, CancellationToken cancellationToken)
    {
        var report = await _catalog.GetByIdAsync(id, cancellationToken);
        if (report is null)
        {
            return NotFound(new ErrorResponse(
                "REPORT_NOT_FOUND",
                $"Report '{id}' is not registered in the catalog.",
                HttpContext.TraceIdentifier,
                DateTime.UtcNow));
        }

        Response.Headers["X-Api-Version"] = "1.0";
        return Ok(report);
    }

    /// <summary>
    /// Executes a report and returns the result in the requested format.
    /// Sprint 1: endpoint stub — pipeline not yet wired (Sprint 2).
    /// </summary>
    [HttpPost("{id}/execute")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Execute(
        string id,
        [FromBody] ReportExecuteRequest request,
        CancellationToken cancellationToken)
    {
        var report = await _catalog.GetByIdAsync(id, cancellationToken);
        if (report is null)
        {
            return NotFound(new ErrorResponse(
                "REPORT_NOT_FOUND",
                $"Report '{id}' is not registered in the catalog.",
                HttpContext.TraceIdentifier,
                DateTime.UtcNow));
        }

        // TODO T11: Wire ReportPipelineService here in Sprint 2
        return StatusCode(StatusCodes.Status501NotImplemented,
            new ErrorResponse("NOT_IMPLEMENTED",
                "Report execution is not yet available (Sprint 2).",
                HttpContext.TraceIdentifier,
                DateTime.UtcNow));
    }

    /// <summary>
    /// Previews a report — always returns JSON regardless of format.
    /// Sprint 1: endpoint stub — pipeline not yet wired (Sprint 2).
    /// </summary>
    [HttpPost("{id}/preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(
        string id,
        [FromBody] ReportExecuteRequest request,
        CancellationToken cancellationToken)
    {
        var report = await _catalog.GetByIdAsync(id, cancellationToken);
        if (report is null)
        {
            return NotFound(new ErrorResponse(
                "REPORT_NOT_FOUND",
                $"Report '{id}' is not registered in the catalog.",
                HttpContext.TraceIdentifier,
                DateTime.UtcNow));
        }

        // TODO T23: Wire JsonRenderer preview in Sprint 3
        return StatusCode(StatusCodes.Status501NotImplemented,
            new ErrorResponse("NOT_IMPLEMENTED",
                "Report preview is not yet available (Sprint 2).",
                HttpContext.TraceIdentifier,
                DateTime.UtcNow));
    }
}
