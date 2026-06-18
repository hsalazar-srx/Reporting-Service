using Reporting.Core.Catalog;

namespace Reporting.Core.Pipeline;

/// <summary>
/// Renders a ReportDataSet to a specific output format (json | excel | pdf).
/// Uses skill: data/report-generation v1.0
/// </summary>
public interface IRenderer
{
    /// <summary>
    /// Format token this renderer produces: json | excel | pdf.
    /// Matched against the requested format by ReportPipelineService.
    /// </summary>
    string Format { get; }

    /// <summary>
    /// Renders the dataset to bytes in this renderer's format.
    /// </summary>
    Task<ReportOutput> RenderAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        CancellationToken cancellationToken = default);
}
