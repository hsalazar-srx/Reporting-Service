using System.Data.Odbc;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.InventoryManagement;

/// <summary>
/// Fetches the Inventory List: every item not in the excluded status, with its item group
/// description. Replaces the legacy "Inventory List.dqy" Excel query, which read the same
/// data from MovexDatawarehouse (SQL Server 2005) and broke when desktops moved to TLS 1.2.
///
/// Report: inventory.item-list
/// Tables: {schema}.MITMAS, {schema}.CSYTAB (CTSTCO = 'ITGR')
/// Parameters: companyCode, excludeStatus (default '90'), itemGroup (optional)
///
/// Differences from the legacy query, both of which can change the row count:
///   - MMCONO is filtered. The warehouse had company flattened; MITMAS is multi-company.
///   - Items with no ITGR code-table row are kept (description blank). The legacy query
///     inner-joined and silently dropped them.
///
/// The schema is per-company (CONO 100 = MVXCDTA, CONO 300 = MVXC300), so it is resolved
/// from the company on every request rather than taken from DataSources:Db2:Schema.
/// The default company comes from environment config, never the catalog.
///
/// Parameters are bound explicitly on an OdbcCommand, as Db2ExchangeRateReader does.
///
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class ItemListFetcher : Db2DirectFetcher
{
    public const string Id = "inventory.item-list";

    /// <summary>Column headers, verbatim from the legacy .dqy so the workbook is familiar.</summary>
    public static readonly IReadOnlyList<string> Columns =
        ["Item number", "Name", "Status", "Item group", "Item group Description"];

    private const string DefaultExcludeStatus = "90";

    private static readonly Regex SafeIdentifier = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, string> _companySchemas;
    private readonly string _defaultCompany;

    public override string ReportId => Id;

    public ItemListFetcher(
        string connectionString,
        IReadOnlyDictionary<string, string> companySchemas,
        string defaultCompany,
        int commandTimeoutSeconds,
        ILogger<ItemListFetcher> logger)
        : base(connectionString, ResolveDefaultSchema(companySchemas, defaultCompany), commandTimeoutSeconds, logger)
    {
        foreach (var (company, schema) in companySchemas)
        {
            if (!SafeIdentifier.IsMatch(schema))
                throw new InvalidOperationException(
                    $"DB2 schema '{schema}' for company {company} is not a plain identifier.");
        }

        _companySchemas = companySchemas;
        _defaultCompany = defaultCompany.Trim();
    }

    /// <summary>
    /// Builds the SQL and its positional parameters. Separated from execution so the query
    /// shape can be tested without DB2.
    /// </summary>
    public ItemListQuery BuildQuery(ReportDefinition report, IReadOnlyDictionary<string, string?> parameters)
    {
        var company = ValueOrDefault(parameters, "companyCode", _defaultCompany);
        if (!_companySchemas.TryGetValue(company, out var schema))
            throw new ArgumentException(
                $"Company {company} has no DB2 schema configured (DataSources:Db2:CompanySchemas).");

        var excludeStatus = ValueOrDefault(parameters, "excludeStatus", DefaultExcludeStatus);
        var itemGroup = ValueOrDefault(parameters, "itemGroup", string.Empty);

        // Order must match the ? placeholders below.
        var values = new List<string> { company, excludeStatus };

        var itemGroupFilter = string.Empty;
        if (itemGroup.Length > 0)
        {
            itemGroupFilter = "\n   AND m.MMITGR = ?";
            values.Add(itemGroup);
        }

        // One row beyond the cap, so a result that would be truncated is detected and refused
        // (EnsureWithinRowLimit) instead of being returned as if complete.
        var rowCap = report.MaxRows > 0 ? $"\n FETCH FIRST {report.MaxRows + 1} ROWS ONLY" : string.Empty;

        // CSYTAB is keyed by division and language as well as the code, so the description
        // is a single-row subquery — a join could return one row per division/language and
        // duplicate items. Blank division and language sort first, preferring company-level text.
        var sql = $"""
            SELECT TRIM(m.MMITNO) AS "Item number",
                   TRIM(m.MMITDS) AS "Name",
                   TRIM(m.MMSTAT) AS "Status",
                   TRIM(m.MMITGR) AS "Item group",
                   (SELECT TRIM(t.CTTX40)
                      FROM {schema}.CSYTAB t
                     WHERE t.CTCONO = m.MMCONO
                       AND t.CTSTCO = 'ITGR'
                       AND t.CTSTKY = m.MMITGR
                     ORDER BY t.CTDIVI, t.CTLNCD
                     FETCH FIRST 1 ROW ONLY) AS "Item group Description"
              FROM {schema}.MITMAS m
             WHERE m.MMCONO = ?
               AND m.MMSTAT <> ?{itemGroupFilter}
             ORDER BY m.MMITNO{rowCap}
            """;

        return new ItemListQuery(sql, values, company);
    }

    protected override async Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(report, parameters);

        await using var cmd = new OdbcCommand(query.Sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var value in query.Parameters)
            cmd.Parameters.Add("?", OdbcType.VarChar).Value = value;

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(Columns.Count);
            for (var i = 0; i < Columns.Count; i++)
                row[Columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString()?.Trim();
            rows.Add(row);
        }

        Logger.LogInformation("{ReportId} returned {Rows} rows for company {Company}",
            ReportId, rows.Count, query.Company);

        EnsureWithinRowLimit(rows.Count, report.MaxRows);

        return new ReportDataSet
        {
            ReportId = ReportId,
            ExecutedAtUtc = executedAtUtc,
            Columns = Columns,
            Rows = rows
        };
    }

    /// <summary>
    /// Refuses a result larger than the catalog cap. A partial item list presented as complete
    /// is worse than an error, so the caller is told to narrow the report instead.
    /// </summary>
    public static void EnsureWithinRowLimit(int rowCount, int maxRows)
    {
        if (maxRows > 0 && rowCount > maxRows)
            throw new ReportValidationException(Id,
            [
                $"The report has more than {maxRows} rows, so it was stopped rather than returned " +
                "incomplete. Filter by Item Group to narrow it."
            ]);
    }

    private static string ResolveDefaultSchema(IReadOnlyDictionary<string, string> companySchemas, string defaultCompany)
    {
        if (!companySchemas.TryGetValue(defaultCompany.Trim(), out var schema))
            throw new InvalidOperationException(
                $"Default company {defaultCompany} has no DB2 schema configured (DataSources:Db2:CompanySchemas).");
        return schema;
    }

    private static string ValueOrDefault(
        IReadOnlyDictionary<string, string?> parameters, string key, string defaultValue)
    {
        parameters.TryGetValue(key, out var raw);
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : raw.Trim();
    }
}

/// <summary>SQL text, positional parameter values in placeholder order, and the resolved company.</summary>
public sealed record ItemListQuery(string Sql, IReadOnlyList<string> Parameters, string Company);
