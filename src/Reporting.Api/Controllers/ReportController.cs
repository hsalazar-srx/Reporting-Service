using Microsoft.AspNetCore.Mvc;
using Reporting.Api.Models;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

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
    private readonly ReportPipelineService _pipeline;
    private readonly ILogger<ReportController> _logger;

    public ReportController(
        IReportCatalogProvider catalog,
        ReportPipelineService pipeline,
        ILogger<ReportController> logger)
    {
        _catalog = catalog;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>
    /// Lists all reports, optionally filtered by domain.
    /// </summary>
    /// <param name="domain">Optional domain filter: CostManagement | SupplyChainPerformance |
    /// Finance | InventoryManagement | Procurement | Production</param>
    /// <param name="cancellationToken">Cancellation token</param>
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
    /// <param name="cancellationToken">Cancellation token</param>
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

        var parameters = (IReadOnlyDictionary<string, string?>)
            request.Parameters.ToDictionary(k => k.Key, v => (string?)v.Value);

        ReportOutput? output;
        try
        {
            output = await _pipeline.ExecuteAsync(report.Id, request.Format ?? string.Empty, parameters, cancellationToken);
        }
        catch (ReportValidationException ex)
        {
            return BadRequest(new ErrorResponse("PARAMETER_VALIDATION_FAILED",
                string.Join("; ", ex.Errors), HttpContext.TraceIdentifier, DateTime.UtcNow));
        }
        catch (ReportFormatException ex)
        {
            return BadRequest(new ErrorResponse("UNSUPPORTED_FORMAT",
                ex.Message, HttpContext.TraceIdentifier, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report execution failed for {ReportId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("DATA_SOURCE_UNAVAILABLE",
                    "Report execution failed. Check data source connectivity.",
                    HttpContext.TraceIdentifier, DateTime.UtcNow));
        }

        if (output is null)
            return NotFound(new ErrorResponse("REPORT_NOT_FOUND",
                $"Report '{id}' is not registered in the catalog.",
                HttpContext.TraceIdentifier, DateTime.UtcNow));

        Response.Headers["X-Api-Version"] = "1.0";

        if (output.Format == "json")
            return Content(System.Text.Encoding.UTF8.GetString(output.Content), output.ContentType);

        return File(output.Content, output.ContentType, output.FileName);
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

        var parameters = (IReadOnlyDictionary<string, string?>)
            request.Parameters.ToDictionary(k => k.Key, v => (string?)v.Value);

        ReportOutput? output;
        try
        {
            output = await _pipeline.PreviewAsync(report.Id, parameters, cancellationToken);
        }
        catch (ReportValidationException ex)
        {
            return BadRequest(new ErrorResponse("PARAMETER_VALIDATION_FAILED",
                string.Join("; ", ex.Errors), HttpContext.TraceIdentifier, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Report preview failed for {ReportId}", id);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new ErrorResponse("DATA_SOURCE_UNAVAILABLE",
                    "Report preview failed. Check data source connectivity.",
                    HttpContext.TraceIdentifier, DateTime.UtcNow));
        }

        if (output is null)
            return NotFound(new ErrorResponse("REPORT_NOT_FOUND",
                $"Report '{id}' is not registered in the catalog.",
                HttpContext.TraceIdentifier, DateTime.UtcNow));

        Response.Headers["X-Api-Version"] = "1.0";
        return Content(System.Text.Encoding.UTF8.GetString(output.Content), "application/json");
    }
}
