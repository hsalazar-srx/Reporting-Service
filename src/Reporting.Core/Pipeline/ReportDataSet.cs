namespace Reporting.Core.Pipeline;

/// <summary>
/// The central data container flowing through the pipeline.
/// Produced by IDataFetcher, consumed by ITransformer and IRenderer.
/// SubReports models Smartbook compound reports — each section is a nested ReportDataSet.
///
/// Uses skill: data/report-generation v1.0
/// </summary>
public sealed record ReportDataSet
{
    /// <summary>Report ID from the catalog (e.g., "cost.average-cost-snapshot").</summary>
    public required string ReportId { get; init; }

    /// <summary>
    /// Stamped at the moment ReportPipelineService begins the fetch stage.
    /// Appears in PDF header ("Data as at: ...") and Excel header row.
    /// </summary>
    public required DateTime ExecutedAtUtc { get; init; }

    /// <summary>Ordered column names corresponding to each row's keys.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Data rows — each row is a dictionary of column name → value.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; } = [];

    /// <summary>
    /// Nested Smartbook subreport sections.
    /// Maps to additional PDF page sections or Excel worksheets.
    /// Empty for simple (non-Smartbook) reports.
    /// </summary>
    public IReadOnlyList<ReportDataSet> SubReports { get; init; } = [];

    /// <summary>Display label for this section (used as worksheet name and PDF section header).</summary>
    public string SectionLabel { get; init; } = string.Empty;
}
