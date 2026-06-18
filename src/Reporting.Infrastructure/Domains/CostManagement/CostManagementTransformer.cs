using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Domains.CostManagement;

/// <summary>
/// Pass-through transformer for all Cost Management reports.
///
/// Data shaping (grouping, subtotals, flags) is handled in the fetcher SQL and in
/// SM-Portal's Recharts layer. The transformer exists as an extension point for
/// future cross-report calculations (e.g., multi-facility rollup subtotals).
///
/// ReportId = "*" registers this as the default transformer for any report
/// that doesn't have a dedicated one.
///
/// Uses skill: data/cost-management-reporting v1.0
/// </summary>
public sealed class CostManagementTransformer : ITransformer
{
    // Covers all three Cost Management reports — extend per-report if needed
    public string ReportId => "*";

    public Task<ReportDataSet> TransformAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default)
        => Task.FromResult(dataSet);
}
