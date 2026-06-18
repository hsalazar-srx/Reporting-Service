using System.Data.Odbc;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.CostManagement;

/// <summary>
/// Fetches Average Cost Snapshot data from MITFAC joined to MITMAS.
/// Supports multi-facility and item status filtering via positional ODBC parameters.
///
/// Report: cost.average-cost-snapshot
/// Tables: mvxcdta.MITFAC, mvxcdta.MITMAS
/// Parameters: asAtDate (unused at DB level — WAC is current), facilities (multiselect),
///             companyCode, itemStatus (multiselect)
///
/// Note: MITFAC holds the current WAC (CFUCOS) and standard cost (CFACSO).
/// There is no point-in-time WAC snapshot in MITFAC — asAtDate is captured in ExecutedAtUtc
/// for the report header. Point-in-time WAC history is in FCAAVP (WacHistoryFetcher).
///
/// Uses skill: data/cost-management-reporting v1.0
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class AverageCostFetcher : Db2DirectFetcher
{
    public override string ReportId => "cost.average-cost-snapshot";

    public AverageCostFetcher(
        string connectionString,
        string schema,
        int commandTimeoutSeconds,
        ILogger<AverageCostFetcher> logger)
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

        // Build IN clause placeholders — positional ? per value (ODBC requirement)
        var facilityPlaceholders = string.Join(", ", facilities.Select(_ => "?"));
        var statusPlaceholders = string.Join(", ", itemStatuses.Select(_ => "?"));

        var sql = $"""
            SELECT
                TRIM(f.CFFACI)  AS Facility,
                TRIM(f.CFITNO)  AS ItemNumber,
                TRIM(m.MMITDS)  AS ItemDescription,
                TRIM(m.MMITGR)  AS ItemGroup,
                f.CFUCOS        AS WacCost,
                f.CFACSO        AS StandardCost
            FROM {Schema}.MITFAC f
            JOIN {Schema}.MITMAS m
              ON TRIM(m.MMITNO) = TRIM(f.CFITNO)
            WHERE f.CFFACI IN ({facilityPlaceholders})
              AND m.MMSTAT IN ({statusPlaceholders})
            ORDER BY f.CFFACI, m.MMITGR, f.CFITNO
            FETCH FIRST {report.MaxRows} ROWS ONLY
            """;

        // Positional params: facilities first, then statuses — order must match SQL
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

    private static ReportDataSet BuildDataSet(
        string reportId,
        DateTime executedAtUtc,
        IList<dynamic> rows)
    {
        if (rows.Count == 0)
        {
            return new ReportDataSet
            {
                ReportId = reportId,
                ExecutedAtUtc = executedAtUtc,
                Columns = ["Facility", "ItemNumber", "ItemDescription", "ItemGroup", "WacCost", "StandardCost"],
                Rows = []
            };
        }

        var columns = ((IDictionary<string, object>)rows[0]).Keys.ToList().AsReadOnly();

        var dataRows = rows
            .Select(row => (IReadOnlyDictionary<string, object?>)
                ((IDictionary<string, object>)row).ToDictionary(
                    k => k.Key,
                    v => (object?)v.Value))
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
