using System.Data.Odbc;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.CostManagement;

/// <summary>
/// Fetches WAC History data from FCAAVP using a CTE with ROW_NUMBER for ordering.
/// Supports facility + optional item number filter and a date range.
///
/// Report: cost.wac-history
/// Tables: mvxcdta.FCAAVP, mvxcdta.MITMAS (join for item description)
/// Parameters: facility (required), dateFrom (required), dateTo (required),
///             itemNumber (optional filter), companyCode
///
/// AVTRDT is stored as INT YYYYMMDD — converted via MovexDateConverter.
///
/// Uses skill: data/cost-management-reporting v1.0
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class WacHistoryFetcher : Db2DirectFetcher
{
    public override string ReportId => "cost.wac-history";

    public WacHistoryFetcher(
        string connectionString,
        string schema,
        int commandTimeoutSeconds,
        ILogger<WacHistoryFetcher> logger)
        : base(connectionString, schema, commandTimeoutSeconds, logger) { }

    protected override async Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken)
    {
        parameters.TryGetValue("facility", out var facility);
        parameters.TryGetValue("dateFrom", out var dateFromStr);
        parameters.TryGetValue("dateTo", out var dateToStr);
        parameters.TryGetValue("itemNumber", out var itemNumber);

        var dateFrom = MovexDateConverter.ParseParamToMovexInt(dateFromStr!);
        var dateTo = MovexDateConverter.ParseParamToMovexInt(dateToStr!);

        var hasItemFilter = !string.IsNullOrWhiteSpace(itemNumber);

        // CTE groups by item + facility, ordered by date desc — ROW_NUMBER gives history sequence
        var sql = $"""
            WITH WAC_History AS (
                SELECT
                    TRIM(h.AVFACI)  AS Facility,
                    TRIM(h.AVITNO)  AS ItemNumber,
                    h.AVTRDT        AS TransactionDateInt,
                    h.AVTRUS        AS WacCost,
                    ROW_NUMBER() OVER (
                        PARTITION BY h.AVFACI, h.AVITNO
                        ORDER BY h.AVTRDT DESC
                    ) AS HistorySeq
                FROM {Schema}.FCAAVP h
                WHERE h.AVFACI = ?
                  AND h.AVTRDT BETWEEN ? AND ?
                  {(hasItemFilter ? "AND TRIM(h.AVITNO) = ?" : "")}
            )
            SELECT
                w.Facility,
                w.ItemNumber,
                TRIM(m.MMITDS)  AS ItemDescription,
                TRIM(m.MMITGR)  AS ItemGroup,
                w.TransactionDateInt,
                w.WacCost,
                w.HistorySeq
            FROM WAC_History w
            LEFT JOIN {Schema}.MITMAS m
              ON TRIM(m.MMITNO) = w.ItemNumber
            ORDER BY w.ItemNumber, w.TransactionDateInt DESC
            FETCH FIRST {report.MaxRows} ROWS ONLY
            """;

        var paramList = new List<object?> { facility, dateFrom, dateTo };
        if (hasItemFilter)
            paramList.Add(itemNumber!.Trim());

        var rows = (await QueryAsync<dynamic>(connection, sql, paramList.ToArray(), cancellationToken)
                        .ConfigureAwait(false)).ToList();

        return BuildDataSet(ReportId, executedAtUtc, rows);
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
                           "TransactionDateInt", "WacCost", "HistorySeq"],
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
