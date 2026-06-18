using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Pipeline;

/// <summary>
/// Orchestrates the full report pipeline: Catalog Lookup → Parameter Validation →
/// Data Fetch → Transform → Render → ReportOutput.
///
/// Fetchers and transformers are registered by ReportId; renderers by Format.
/// ReportPipelineService resolves the correct implementation at runtime.
///
/// Uses skill: data/report-generation v1.0
/// </summary>
public sealed class ReportPipelineService
{
    private readonly IReportCatalogProvider _catalog;
    private readonly IParameterValidator _validator;
    private readonly IReadOnlyDictionary<string, IDataFetcher> _fetchers;
    private readonly IReadOnlyDictionary<string, ITransformer> _transformers;
    private readonly IReadOnlyDictionary<string, IRenderer> _renderers;
    private readonly ILogger<ReportPipelineService> _logger;

    public ReportPipelineService(
        IReportCatalogProvider catalog,
        IParameterValidator validator,
        IEnumerable<IDataFetcher> fetchers,
        IEnumerable<ITransformer> transformers,
        IEnumerable<IRenderer> renderers,
        ILogger<ReportPipelineService> logger)
    {
        _catalog = catalog;
        _validator = validator;
        _fetchers = fetchers.ToDictionary(f => f.ReportId, StringComparer.OrdinalIgnoreCase);
        _transformers = transformers.ToDictionary(t => t.ReportId, StringComparer.OrdinalIgnoreCase);
        _renderers = renderers.ToDictionary(r => r.Format, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// Executes a report in the requested format.
    /// Returns null if the report ID is not in the catalog.
    /// Throws <see cref="ReportValidationException"/> if parameters are invalid.
    /// Throws <see cref="ReportFormatException"/> if the format is unsupported.
    /// </summary>
    public async Task<ReportOutput?> ExecuteAsync(
        string reportId,
        string format,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default)
    {
        var report = await _catalog.GetByIdAsync(reportId, cancellationToken).ConfigureAwait(false);
        if (report is null)
            return null;

        // Parameter validation
        var errors = _validator.Validate(report, parameters);
        if (errors.Count > 0)
            throw new ReportValidationException(reportId, errors);

        // Format check
        var effectiveFormat = string.IsNullOrWhiteSpace(format) ? report.DefaultFormat : format;
        if (!report.SupportedFormats.Contains(effectiveFormat, StringComparer.OrdinalIgnoreCase))
            throw new ReportFormatException(reportId, effectiveFormat, report.SupportedFormats);

        if (!_renderers.TryGetValue(effectiveFormat, out var renderer))
            throw new ReportFormatException(reportId, effectiveFormat, report.SupportedFormats);

        // Fetch
        if (!_fetchers.TryGetValue(reportId, out var fetcher))
            throw new InvalidOperationException(
                $"No IDataFetcher registered for report '{reportId}'. " +
                $"Register a fetcher in DI with ReportId = \"{reportId}\".");

        _logger.LogInformation("Executing report {ReportId} format={Format}", reportId, effectiveFormat);

        var dataSet = await fetcher.FetchAsync(report, parameters, cancellationToken).ConfigureAwait(false);

        // Transform — fall back to pass-through if no domain transformer registered
        if (_transformers.TryGetValue(reportId, out var transformer) ||
            _transformers.TryGetValue("*", out transformer))
        {
            dataSet = await transformer.TransformAsync(dataSet, report, parameters, cancellationToken)
                                       .ConfigureAwait(false);
        }

        // Render
        var output = await renderer.RenderAsync(dataSet, report, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Report {ReportId} executed: format={Format} rows={Rows} bytes={Bytes}",
            reportId, effectiveFormat, dataSet.Rows.Count, output.Content.Length);

        return output;
    }

    /// <summary>
    /// Executes a report and forces JSON output regardless of the requested format.
    /// Used by the /preview endpoint.
    /// </summary>
    public Task<ReportOutput?> PreviewAsync(
        string reportId,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(reportId, "json", parameters, cancellationToken);
}
