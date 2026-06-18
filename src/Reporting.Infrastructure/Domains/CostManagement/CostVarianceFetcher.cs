using System.Data.Odbc;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.CostManagement;

/// <summary>
/// Fetches Cost Variance Analysis data — standard cost vs WAC per item per facility.
/// Flags items with zero WAC (IsZeroCost).
///
/// Report: cost.cost-variance-analysis
/// Tables: mvxcdta.MITFAC, mvxcdta.MITMAS
/// Parameters: facilities (multiselect), companyCode, includeZeroCostOnly, itemStatus
///
/// Uses skill: data/cost-management-reporting v1.0
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class CostVarianceFetcher : Db2DirectFetcher
{
    public override string ReportId => "cost.cost-variance-analysis";

    public CostVarianceFetcher(
        string connectionString,
        string schema,
        int commandTimeoutSeconds,
        ILogger<CostVarianceFetcher> logger)
        : base(connectionString, schema, commandTimeoutSeconds, logger) { }

    protected override async Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken)
    {
        var facilities = ParseMultiselect(parameters, "facilities", defaultValue: "001");
        var itemStatuses = ParseMultiselect(parameters, "itemStatus", defaultValue: "20");
        parameters.TryGetValue("includeZeroCostOnly", out var zeroCostOnlyStr);
        var zeroCostOnly = string.Equals(zeroCostOnlyStr, "true", StringComparison.OrdinalIgnoreCase);

        var facilityPlaceholders = string.Join(", ", facilities.Select(_ => "?"));
        var statusPlaceholders = string.Join(", ", itemStatuses.Select(_ => "?"));

        var zeroCostFilter = zeroCostOnly ? "AND f.CFUCOS = 0" : "";

        var sql = $"""
            SELECT
                TRIM(f.CFFACI)      AS Facility,
                TRIM(f.CFITNO)      AS ItemNumber,
                TRIM(m.MMITDS)      AS ItemDescription,
                TRIM(m.MMITGR)      AS ItemGroup,
                f.CFUCOS            AS WacCost,
                f.CFACSO            AS StandardCost,
                CASE WHEN f.CFUCOS = 0 THEN 1 ELSE 0 END AS IsZeroCost,
                CASE
                    WHEN f.CFACSO = 0 THEN NULL
                    ELSE (f.CFUCOS - f.CFACSO) / f.CFACSO * 100
                END AS VariancePct
            FROM {Schema}.MITFAC f
            JOIN {Schema}.MITMAS m
              ON TRIM(m.MMITNO) = TRIM(f.CFITNO)
            WHERE f.CFFACI IN ({facilityPlaceholders})
              AND m.MMSTAT IN ({statusPlaceholders})
              {zeroCostFilter}
            ORDER BY f.CFFACI, m.MMITGR, f.CFITNO
            FETCH FIRST {report.MaxRows} ROWS ONLY
            """;

        var paramValues = facilities.Concat(itemStatuses).Cast<object>().ToArray();

        var rows = (await QueryAsync<dynamic>(connection, sql, paramValues, cancellationToken)
                        .ConfigureAwait(false)).ToList();

        return BuildDataSet(ReportId, executedAtUtc, rows);
    }

    private static IReadOnlyList<string> ParseMultiselect(
        IReadOnlyDictionary<string, string?> parameters,
        string key,
        string defaultValue)
    {
        parameters.TryGetValue(key, out var raw);
        if (string.IsNullOrWhiteSpace(raw))
            return [defaultValue];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static ReportDataSet BuildDataSet(string reportId, DateTime executedAtUtc, IList<dynamic> rows)
    {
        if (rows.Count == 0)
        {
            return new ReportDataSet
            {
                ReportId = reportId,
                ExecutedAtUtc = executedAtUtc,
                Columns = ["Facility", "ItemNumber", "ItemDescription", "ItemGroup",
                           "WacCost", "StandardCost", "IsZeroCost", "VariancePct"],
                Rows = []
            };
        }

        var columns = ((IDictionary<string, object>)rows[0]).Keys.ToList().AsReadOnly();
        var dataRows = rows
            .Select(row => (IReadOnlyDictionary<string, object?>)
                ((IDictionary<string, object>)row).ToDictionary(k => k.Key, v => (object?)v.Value))
            .ToList()
            .AsReadOnly();

        return new ReportDataSet
        {
            ReportId = reportId,
            ExecutedAtUtc = executedAtUtc,
            Columns = columns,
            Rows = dataRows
        };
    }
}
