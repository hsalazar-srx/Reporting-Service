using Reporting.Core.Catalog;

namespace Reporting.Core.Pipeline;

/// <summary>
/// Fetches raw data from a data source (DB2 or SQL Server) for a specific report.
/// Each domain report implements its own fetcher.
/// Uses skill: data/report-generation v1.0
/// </summary>
public interface IDataFetcher
{
    /// <summary>
    /// Data source token this fetcher handles: Db2Direct | SqlServerDw.
    /// Matched against ReportDefinition.DataSource by ReportPipelineService.
    /// </summary>
    string DataSource { get; }

    /// <summary>
    /// Report ID this fetcher handles (e.g., "cost.average-cost-snapshot").
    /// Matched against ReportDefinition.Id by ReportPipelineService.
    /// </summary>
    string ReportId { get; }

    /// <summary>
    /// Fetches raw data and returns a populated ReportDataSet.
    /// ExecutedAtUtc must be stamped at the start of this method (T12a).
    /// </summary>
    Task<ReportDataSet> FetchAsync(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default);
}
