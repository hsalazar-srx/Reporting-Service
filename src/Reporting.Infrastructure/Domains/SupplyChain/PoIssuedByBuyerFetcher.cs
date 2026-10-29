using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.SupplyChain;

/// <summary>
/// POs issued by buyer, per day and per week, from the PO header (MPHEAD).
///
/// Report: supplychain.po-issued-by-buyer
/// Table:  {schema}.MPHEAD — columns verified against QSYS2.SYSCOLUMNS by Hector, 2026-10-23:
///         IABUYE buyer, IALEDT first printout date (= "issued"), IARGDT entry date (= created),
///         IANOLN number of PO lines, IAPUSL / IAPUST lowest / highest status.
/// Rules:  from the DBSRV check of 2026-10-23 (POs entered since 1 Aug 2026):
///           - lowest status 00 is a deleted PO (highest 99, no lines) and is left out;
///           - the header line count matches the line table for 95% of POs, so it is used as is;
///           - 84% of POs are printed the day they are entered, so "issued" barely differs from "created".
/// Output: PoIssuedByBuyerWorkbook.
///
/// Parameters are bound explicitly on an OdbcCommand, as InventoryValuationFetcher does.
///
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class PoIssuedByBuyerFetcher : Db2DirectFetcher
{
    public const string Id = "supplychain.po-issued-by-buyer";

    /// <summary>Weeks shown when no From date is entered: the current week plus this many before it.</summary>
    public const int DefaultWeeks = 12;

    private const string DateFormat = "yyyy-MM-dd";

    private readonly CompanySchemas _companySchemas;
    private readonly Func<DateTime> _today;

    public override string ReportId => Id;

    public PoIssuedByBuyerFetcher(
        string connectionString,
        IReadOnlyDictionary<string, string> companySchemas,
        string defaultCompany,
        int commandTimeoutSeconds,
        ILogger<PoIssuedByBuyerFetcher> logger,
        Func<DateTime>? today = null)
        : base(connectionString, new CompanySchemas(companySchemas, defaultCompany).DefaultSchema,
               commandTimeoutSeconds, logger)
    {
        _companySchemas = new CompanySchemas(companySchemas, defaultCompany);
        _today = today ?? (() => DateTime.Today);
    }

    /// <summary>Builds the SQL and its positional parameters, in placeholder order.</summary>
    public PoIssuedByBuyerQuery BuildQuery(ReportDefinition report, IReadOnlyDictionary<string, string?> parameters)
    {
        var errors = new List<string>();
        var (from, to) = ReadPeriod(parameters, errors);
        var divisions = ReadChoices(report, parameters, "divisions", errors);
        var orderTypes = ReadChoices(report, parameters, "orderTypes", errors);
        if (errors.Count > 0) throw new ReportValidationException(Id, errors);

        var (company, schema) = _companySchemas.Resolve(Value(parameters, "companyCode"));
        var fromInt = MovexDateConverter.ToMovexInt(from);
        var toInt = MovexDateConverter.ToMovexInt(to);

        // Order must match the ? placeholders below.
        var values = new List<object> { company };
        values.AddRange(divisions);
        values.AddRange(orderTypes);
        values.AddRange(new object[] { fromInt, toInt, fromInt, toInt });

        // One row beyond the cap, so a result that would be truncated is detected and refused.
        var rowCap = report.MaxRows > 0 ? $"\n FETCH FIRST {report.MaxRows + 1} ROWS ONLY" : string.Empty;

        // A PO is wanted if it was issued (first printed) or created in the period: issued drives the
        // counts, created is shown beside it.
        var sql = $"""
            SELECT TRIM(h.IADIVI) AS DIVISION,
                   TRIM(h.IAWHLO) AS WAREHOUSE,
                   TRIM(h.IAPUNO) AS PO_NUMBER,
                   TRIM(h.IAORTY) AS ORDER_TYPE,
                   TRIM(h.IABUYE) AS BUYER,
                   TRIM(h.IASUNO) AS SUPPLIER,
                   h.IARGDT AS ENTRY_DATE,
                   h.IALEDT AS FIRST_PRINTOUT_DATE,
                   h.IANOLN AS PO_LINES,
                   TRIM(h.IAPUSL) AS LOWEST_STATUS,
                   TRIM(h.IAPUST) AS HIGHEST_STATUS
              FROM {schema}.MPHEAD h
             WHERE h.IACONO = ?
               AND h.IAPUSL <> '00'
               AND h.IADIVI IN ({Placeholders(divisions.Count)})
               AND h.IAORTY IN ({Placeholders(orderTypes.Count)})
               AND ((h.IALEDT BETWEEN ? AND ?) OR (h.IARGDT BETWEEN ? AND ?))
             ORDER BY h.IADIVI, h.IABUYE, h.IAPUNO{rowCap}
            """;

        return new PoIssuedByBuyerQuery(sql, values,
            new PoIssuedRun(company, schema, from, to, divisions, orderTypes));
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
        {
            if (value is int number) cmd.Parameters.Add("?", OdbcType.Int).Value = number;
            else cmd.Parameters.Add("?", OdbcType.VarChar).Value = value;
        }

        var orders = new List<PurchaseOrderHeader>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            orders.Add(new PurchaseOrderHeader(
                Division: Text(reader, 0),
                Warehouse: Text(reader, 1),
                PoNumber: Text(reader, 2),
                OrderType: Text(reader, 3),
                Buyer: Text(reader, 4),
                Supplier: Text(reader, 5),
                EntryDate: Date(reader, 6),
                FirstPrintoutDate: Date(reader, 7),
                Lines: reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                LowestStatus: Text(reader, 9),
                HighestStatus: Text(reader, 10)));
        }

        Logger.LogInformation("{ReportId} returned {Rows} POs for company {Company} {From:yyyy-MM-dd}..{To:yyyy-MM-dd}",
            ReportId, orders.Count, query.Run.Company, query.Run.From, query.Run.To);

        EnsureWithinRowLimit(orders.Count, report.MaxRows);

        return PoIssuedByBuyerWorkbook.Build(ReportId, report.DisplayName, orders, query.Run, executedAtUtc);
    }

    /// <summary>Refuses a truncated result rather than returning partial counts.</summary>
    public static void EnsureWithinRowLimit(int rowCount, int maxRows)
    {
        if (maxRows > 0 && rowCount > maxRows)
            throw new ReportValidationException(Id,
            [
                $"The period has more than {maxRows} POs, so the report was stopped rather than " +
                "returned incomplete. Choose a shorter period."
            ]);
    }

    /// <summary>
    /// From and To as entered, or the default: To = today, From = the Monday DefaultWeeks weeks before
    /// this week's Monday, so the file shows that many full weeks plus the current one.
    /// </summary>
    private (DateTime From, DateTime To) ReadPeriod(IReadOnlyDictionary<string, string?> parameters, List<string> errors)
    {
        var today = _today().Date;
        var to = ParseDate(parameters, "toDate", errors) ?? today;
        var from = ParseDate(parameters, "fromDate", errors) ?? WeekStart(to).AddDays(-7 * DefaultWeeks);

        if (from > to)
            errors.Add($"From date {from:yyyy-MM-dd} is after To date {to:yyyy-MM-dd}.");
        return (from, to);
    }

    private static DateTime? ParseDate(IReadOnlyDictionary<string, string?> parameters, string key, List<string> errors)
    {
        var raw = Value(parameters, key);
        if (raw is null) return null;
        if (DateTime.TryParseExact(raw, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        errors.Add($"{key} must be a date like 2026-08-03 (got '{raw}').");
        return null;
    }

    /// <summary>Monday of the week containing the date.</summary>
    public static DateTime WeekStart(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>
    /// A multiselect's values as entered, or its catalog default, or all its allowed values. Every value
    /// must be one of the catalog's allowed values — they become bound parameters, but the list of allowed
    /// values is what keeps the report to the divisions and order types it is meant for.
    /// </summary>
    private static IReadOnlyList<string> ReadChoices(
        ReportDefinition report, IReadOnlyDictionary<string, string?> parameters, string name, List<string> errors)
    {
        var param = report.Parameters.FirstOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException($"Catalog entry '{report.Id}' defines no '{name}' parameter.");
        if (param.AllowedValues.Count == 0)
            throw new InvalidOperationException($"Catalog parameter '{report.Id}/{name}' lists no allowed values.");

        var raw = Value(parameters, name) ?? param.DefaultValue;
        if (string.IsNullOrWhiteSpace(raw)) return param.AllowedValues;

        var chosen = new List<string>();
        foreach (var v in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var known = param.AllowedValues.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                errors.Add($"{param.DisplayName} '{v}' is not one of {string.Join(", ", param.AllowedValues)}.");
            else if (!chosen.Contains(known))
                chosen.Add(known);
        }
        return chosen.Count > 0 ? chosen : param.AllowedValues;
    }

    private static string Placeholders(int count) => string.Join(", ", Enumerable.Repeat("?", count));

    private static string? Value(IReadOnlyDictionary<string, string?> parameters, string key) =>
        parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static string Text(System.Data.Common.DbDataReader reader, int i) =>
        reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString()?.Trim() ?? string.Empty;

    // MOVEX dates are numeric YYYYMMDD; 0 means not set (e.g. a PO never printed).
    private static DateTime? Date(System.Data.Common.DbDataReader reader, int i) =>
        reader.IsDBNull(i)
            ? null
            : MovexDateConverter.FromMovexInt(Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture));
}

/// <summary>SQL text, positional parameters in placeholder order, and what the run used.</summary>
public sealed record PoIssuedByBuyerQuery(string Sql, IReadOnlyList<object> Parameters, PoIssuedRun Run);

/// <summary>What a run was produced with — recorded on the Definitions sheet.</summary>
public sealed record PoIssuedRun(
    string Company,
    string Schema,
    DateTime From,
    DateTime To,
    IReadOnlyList<string> Divisions,
    IReadOnlyList<string> OrderTypes);

/// <summary>One PO header as read from MPHEAD.</summary>
public sealed record PurchaseOrderHeader(
    string Division,
    string Warehouse,
    string PoNumber,
    string OrderType,
    string Buyer,
    string Supplier,
    DateTime? EntryDate,
    DateTime? FirstPrintoutDate,
    int Lines,
    string LowestStatus,
    string HighestStatus);
