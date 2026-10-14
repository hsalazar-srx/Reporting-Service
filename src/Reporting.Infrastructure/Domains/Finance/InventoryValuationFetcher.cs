using System.Data.Odbc;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.Finance;

/// <summary>
/// Inventory Valuation — FIFO layer unwind of the WAC history (FCAAVP), valued at the spot rate on
/// each layer's WAC recalculation date and at a revaluation rate, with Movex WAC and standard cost
/// for reconciliation.
///
/// Report: finance.inventory-valuation
/// SQL:    InventoryValuation.sql (embedded) — Karen Lewin's 2026-10-13 query with the methodology
///         unchanged pending Finance validation; deviations are marked [CHANGED] in the file.
/// Rates:  book rates read from CCURRA (latest per currency) on every run; rate types and division
///         from configuration "InventoryValuation". Nothing rate-related is hard-coded.
/// Output: InventoryValuationWorkbook — Summary, FIFO Layers, Reconciliation, Assumptions.
///
/// Parameters are bound explicitly on an OdbcCommand, as Db2ExchangeRateReader does.
///
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public sealed class InventoryValuationFetcher : Db2DirectFetcher
{
    public const string Id = "finance.inventory-valuation";

    private const string ResourceName = "Reporting.Infrastructure.Domains.Finance.InventoryValuation.sql";

    private static readonly Regex Token = new(@"\{\{([A-Z_]+)(?::([^}]+))?\}\}", RegexOptions.Compiled);
    private static readonly Regex ShortCode = new("^[A-Za-z0-9]{1,3}$", RegexOptions.Compiled);

    // Full-line comments are removed before tokens are filled, so a '?' or {{TOKEN}} written in a
    // comment can never count as a placeholder.
    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(InventoryValuationFetcher).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded SQL '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n').Where(l => !l.TrimStart().StartsWith("--"));
        return string.Join('\n', lines);
    });

    private readonly CompanySchemas _companySchemas;
    private readonly InventoryValuationOptions _options;

    public override string ReportId => Id;

    public InventoryValuationFetcher(
        string connectionString,
        IReadOnlyDictionary<string, string> companySchemas,
        string defaultCompany,
        InventoryValuationOptions options,
        int commandTimeoutSeconds,
        ILogger<InventoryValuationFetcher> logger)
        : base(connectionString, new CompanySchemas(companySchemas, defaultCompany).DefaultSchema,
               commandTimeoutSeconds, logger)
    {
        // These are written into SQL text, so they must be short plain codes.
        foreach (var (name, value) in new[]
                 {
                     (nameof(options.RateDivision), options.RateDivision),
                     (nameof(options.SpotRateType), options.SpotRateType),
                     (nameof(options.BookRateType), options.BookRateType)
                 })
        {
            if (!ShortCode.IsMatch(value ?? string.Empty))
                throw new InvalidOperationException(
                    $"{InventoryValuationOptions.SectionName}:{name} '{value}' must be 1-3 letters or digits.");
        }

        _companySchemas = new CompanySchemas(companySchemas, defaultCompany);
        _options = options;
    }

    /// <summary>Latest book rate per currency, for the configured rate type and division.</summary>
    public (string Sql, IReadOnlyList<object> Parameters) BuildBookRateQuery(string schema, string company)
    {
        var sql = $"""
            SELECT R.CUCUCD, R.CUARAT, R.CUCUTD
              FROM {schema}.CCURRA R
             WHERE R.CUCONO = ?
               AND R.CUDIVI = ?
               AND R.CUCRTP = ?
               AND R.CUCUTD = (SELECT MAX(X.CUCUTD)
                                 FROM {schema}.CCURRA X
                                WHERE X.CUCONO = R.CUCONO
                                  AND X.CUDIVI = R.CUDIVI
                                  AND X.CUCRTP = R.CUCRTP
                                  AND X.CUCUCD = R.CUCUCD)
             ORDER BY R.CUCUCD
            """;
        return (sql, [int.Parse(company, CultureInfo.InvariantCulture), _options.RateDivision, _options.BookRateType]);
    }

    /// <summary>Builds the valuation SQL and its positional parameters, in placeholder order.</summary>
    public InventoryValuationQuery BuildQuery(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        IReadOnlyList<BookRate> bookRates)
    {
        if (bookRates.Count == 0)
            throw new InvalidOperationException(
                $"No book rates found in CCURRA (rate type {_options.BookRateType}, division {_options.RateDivision}). " +
                "Without them foreign-currency costs cannot be converted, so the valuation was not run.");

        var (company, schema) = _companySchemas.Resolve(Value(parameters, "companyCode"));
        var warehouses = ParseWarehouses(report, Value(parameters, "warehouses"));
        var itemNumber = Value(parameters, "itemNumber");
        var itemGroup = Value(parameters, "itemGroup");
        var revalueAt = ParseDate(Value(parameters, "revalueAtDate"));

        var values = new List<object>();
        var sql = Token.Replace(Template.Value, match =>
        {
            var arg = match.Groups[2].Value;
            switch (match.Groups[1].Value)
            {
                case "SCHEMA":         return schema;
                case "CONO":           return company;                      // validated digits
                case "RATE_DIVISION":  return _options.RateDivision;        // validated short code
                case "SPOT_RATE_TYPE": return _options.SpotRateType;
                case "BOOK_RATE":      return BookRateCase(arg, bookRates);
                case "WAREHOUSES":
                    values.AddRange(warehouses);
                    return string.Join(", ", warehouses.Select(_ => "?"));
                case "FILTERS":
                    var filters = new StringBuilder();
                    if (itemNumber is not null) { filters.Append("AND IB.MBITNO = ? "); values.Add(itemNumber); }
                    if (itemGroup is not null) { filters.Append("AND ITM.MMITGR = ? "); values.Add(itemGroup); }
                    return filters.ToString();
                case "REVALUE_DATE":
                    if (revalueAt is null) return string.Empty;
                    values.Add(MovexDateConverter.ToMovexInt(revalueAt.Value.ToDateTime(TimeOnly.MinValue)));
                    return $"AND {arg}.CUCUTD <= ?";
                case "ROW_CAP":
                    return report.MaxRows > 0 ? $"FETCH FIRST {report.MaxRows + 1} ROWS ONLY" : string.Empty;
                default:
                    throw new InvalidOperationException($"Unknown SQL token {match.Value}.");
            }
        });

        return new InventoryValuationQuery(sql, values, company, schema, warehouses, itemNumber, itemGroup,
            revalueAt, bookRates, _options);
    }

    protected override async Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken)
    {
        var (company, schema) = _companySchemas.Resolve(Value(parameters, "companyCode"));
        var (rateSql, rateParams) = BuildBookRateQuery(schema, company);
        var bookRates = new List<BookRate>();
        foreach (var row in await ReadAsync(connection, rateSql, rateParams, cancellationToken).ConfigureAwait(false))
        {
            var effective = row["CUCUTD"] is { } d ? MovexDateConverter.FromMovexInt(Convert.ToInt32(d)) : null;
            bookRates.Add(new BookRate((string)row["CUCUCD"]!, Convert.ToDecimal(row["CUARAT"]),
                effective is null ? null : DateOnly.FromDateTime(effective.Value)));
        }

        var query = BuildQuery(report, parameters, bookRates);
        var layers = await ReadAsync(connection, query.Sql, query.Parameters, cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "{ReportId} returned {Rows} layer rows for company {Company} warehouses {Warehouses} using {Rates} book rates",
            ReportId, layers.Count, query.Company, string.Join(",", query.Warehouses), bookRates.Count);

        EnsureWithinRowLimit(layers.Count, report.MaxRows);

        return InventoryValuationWorkbook.Build(ReportId, layers, query.ToRun(), executedAtUtc);
    }

    /// <summary>Refuses a truncated result rather than returning a partial valuation.</summary>
    public static void EnsureWithinRowLimit(int rowCount, int maxRows)
    {
        if (maxRows > 0 && rowCount > maxRows)
            throw new ReportValidationException(Id,
            [
                $"The valuation has more than {maxRows} layer rows, so it was stopped rather than " +
                "returned incomplete. Narrow it by warehouse, item or item group."
            ]);
    }

    private async Task<List<IReadOnlyDictionary<string, object?>>> ReadAsync(
        OdbcConnection connection, string sql, IReadOnlyList<object> parameters, CancellationToken ct)
    {
        await using var cmd = new OdbcCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        foreach (var value in parameters)
        {
            if (value is int number) cmd.Parameters.Add("?", OdbcType.Int).Value = number;
            else cmd.Parameters.Add("?", OdbcType.VarChar).Value = value;
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[reader.GetName(i).ToUpperInvariant()] = value is string s ? s.Trim() : value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string BookRateCase(string expression, IReadOnlyList<BookRate> bookRates)
    {
        var sb = new StringBuilder($"CASE {expression}");
        foreach (var rate in bookRates)
        {
            // Currency codes come from CCURRA; only plain codes are written into the SQL.
            if (!Regex.IsMatch(rate.Currency, "^[A-Z]{3}$")) continue;
            sb.Append($" WHEN '{rate.Currency}' THEN {rate.Rate.ToString("0.000000", CultureInfo.InvariantCulture)}");
        }
        return sb.Append(" ELSE 1.000000 END").ToString();
    }

    private static IReadOnlyList<string> ParseWarehouses(ReportDefinition report, string? raw)
    {
        // The catalog's allowed values are the single list of warehouses in scope.
        var inScope = report.Parameters.FirstOrDefault(p => p.Name == "warehouses")?.AllowedValues ?? [];
        if (inScope.Count == 0)
            throw new InvalidOperationException($"Catalog entry '{report.Id}' lists no warehouses.");
        if (raw is null) return inScope;

        var chosen = new List<string>();
        foreach (var w in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var known = inScope.FirstOrDefault(a => a.Equals(w, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Warehouse '{w}' is not in scope ({string.Join(", ", inScope)}).");
            if (!chosen.Contains(known)) chosen.Add(known);
        }
        return chosen.Count > 0 ? chosen : inScope;
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (raw is null) return null;
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"Revaluation date must be yyyy-MM-dd. Got: {raw}");
    }

    private static string? Value(IReadOnlyDictionary<string, string?> parameters, string key) =>
        parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>SQL text, positional parameters in placeholder order, and what the run used.</summary>
public sealed record InventoryValuationQuery(
    string Sql,
    IReadOnlyList<object> Parameters,
    string Company,
    string Schema,
    IReadOnlyList<string> Warehouses,
    string? ItemNumber,
    string? ItemGroup,
    DateOnly? RevalueAtDate,
    IReadOnlyList<BookRate> BookRates,
    InventoryValuationOptions Options)
{
    public InventoryValuationRun ToRun() => new(Company, Schema, Warehouses, ItemNumber, ItemGroup, RevalueAtDate,
        BookRates, Options.RateDivision, Options.SpotRateType, Options.BookRateType);
}

/// <summary>What a valuation run was produced with — recorded on the Assumptions sheet.</summary>
public sealed record InventoryValuationRun(
    string Company,
    string Schema,
    IReadOnlyList<string> Warehouses,
    string? ItemNumber,
    string? ItemGroup,
    DateOnly? RevalueAtDate,
    IReadOnlyList<BookRate> BookRates,
    string RateDivision,
    string SpotRateType,
    string BookRateType);
