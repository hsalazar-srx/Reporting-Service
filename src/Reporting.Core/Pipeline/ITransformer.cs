using Reporting.Core.Catalog;

namespace Reporting.Core.Pipeline;

/// <summary>
/// Transforms a raw ReportDataSet into a presentation-ready form:
/// grouping, sorting, subtotals, derived columns, zero-cost flags, etc.
/// Each domain report may have its own transformer, or use a pass-through.
/// Uses skill: data/report-generation v1.0
/// </summary>
public interface ITransformer
{
    /// <summary>
    /// Report ID this transformer handles.
    /// Matched against ReportDefinition.Id by ReportPipelineService.
    /// Use "*" to register a pass-through transformer for reports with no transformation needed.
    /// </summary>
    string ReportId { get; }

    /// <summary>
    /// Applies grouping, sorting, subtotals, and derived fields to the raw dataset.
    /// Returns a new ReportDataSet — never mutates the input.
    /// </summary>
    Task<ReportDataSet> TransformAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default);
}
