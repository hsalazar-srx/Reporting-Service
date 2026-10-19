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
/// each layer's WAC recalculation date and at the latest spot rate, with Movex WAC and standard cost
/// for reconciliation, plus material overhead.
///
/// Report: finance.inventory-valuation
/// SQL:    InventoryValuation.sql (embedded) — Karen Lewin's 2026-10-13 query with the methodology
///         unchanged pending Finance validation; deviations are marked [CHANGED] in the file.
/// Rates:  book rates are report parameters (bookRate<CCY>), defaulted in the catalog and adjustable on
///         the report page; spot rate type and division come from configuration "InventoryValuation"
///         and the spot rates from CCURRA. Material overhead is the parameter materialOverheadPercent.
///         Nothing rate-related is hard-coded.
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
    private static readonly Regex CurrencyCode = new("^[A-Z]{3}$", RegexOptions.Compiled);

    private const string BookRatePrefix = "bookRate";
    private const string OverheadParameter = "materialOverheadPercent";

    // Sanity limits on typed numbers. Rates are foreign currency per 1 AUD (JPY is about 93).
    private const decimal MaxBookRate = 100_000m;
    private const decimal MaxOverheadPercent = 1_000m;

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
                     (nameof(options.SpotRateType), options.SpotRateType)
                 })
        {
            if (!ShortCode.IsMatch(value ?? string.Empty))
                throw new InvalidOperationException(
                    $"{InventoryValuationOptions.SectionName}:{name} '{value}' must be 1-3 letters or digits.");
        }

        _companySchemas = new CompanySchemas(companySchemas, defaultCompany);
        _options = options;
    }

    /// <summary>Builds the valuation SQL and its positional parameters, in placeholder order.</summary>
    public InventoryValuationQuery BuildQuery(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters)
    {
        var (bookRates, overheadPercent) = ReadRatesAndOverhead(report, parameters);

        var (company, schema) = _companySchemas.Resolve(Value(parameters, "companyCode"));
        var warehouses = ParseWarehouses(report, Value(parameters, "warehouses"));
        var itemNumber = Value(parameters, "itemNumber");
        var itemGroup = Value(parameters, "itemGroup");

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
                case "ROW_CAP":
                    return report.MaxRows > 0 ? $"FETCH FIRST {report.MaxRows + 1} ROWS ONLY" : string.Empty;
                default:
                    throw new InvalidOperationException($"Unknown SQL token {match.Value}.");
            }
        });

        return new InventoryValuationQuery(sql, values, company, schema, warehouses, itemNumber, itemGroup,
            bookRates, overheadPercent, _options);
    }

    protected override async Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(report, parameters);
        var layers = await ReadAsync(connection, query.Sql, query.Parameters, cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "{ReportId} returned {Rows} layer rows for company {Company} warehouses {Warehouses} using {Rates} book rates",
            ReportId, layers.Count, query.Company, string.Join(",", query.Warehouses), query.BookRates.Count);

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

    /// <summary>
    /// Book rates and material overhead as entered on the report page, or the catalog's default for
    /// any left blank. The catalog is the one place the defaults live.
    /// </summary>
    private static (IReadOnlyList<BookRate> BookRates, decimal OverheadPercent) ReadRatesAndOverhead(
        ReportDefinition report, IReadOnlyDictionary<string, string?> parameters)
    {
        var errors = new List<string>();
        var rates = new List<BookRate>();

        foreach (var param in report.Parameters.Where(p => p.Name.StartsWith(BookRatePrefix, StringComparison.Ordinal)))
        {
            // The currency code is written into SQL text, so it must be three plain capitals.
            var currency = param.Name[BookRatePrefix.Length..];
            if (!CurrencyCode.IsMatch(currency))
                throw new InvalidOperationException(
                    $"Catalog parameter '{param.Name}' must be '{BookRatePrefix}' followed by a 3-letter currency code, e.g. {BookRatePrefix}USD.");

            // The default is the catalog's: a missing or bad one is a configuration error, not the user's.
            var defaultRate = TryNumber(param.DefaultValue, 0m, MaxBookRate, exclusiveMinimum: true)
                ?? throw new InvalidOperationException(
                    $"Catalog parameter '{param.Name}' needs a default rate greater than 0 and below {MaxBookRate:N0}.");
            var entered = Value(parameters, param.Name);
            var rate = entered is null ? defaultRate : TryNumber(entered, 0m, MaxBookRate, exclusiveMinimum: true);

            if (rate is null)
                errors.Add($"{param.DisplayName} must be a number greater than 0 and below {MaxBookRate:N0} (got '{entered}').");
            else
                rates.Add(new BookRate(currency, rate.Value, defaultRate));
        }

        if (rates.Count == 0 && errors.Count == 0)
            throw new InvalidOperationException(
                $"Catalog entry '{report.Id}' defines no book rate parameters ({BookRatePrefix}USD, ...). " +
                "Without them foreign-currency costs cannot be converted, so the valuation was not run.");

        var overheadParam = report.Parameters.FirstOrDefault(p => p.Name == OverheadParameter)
            ?? throw new InvalidOperationException(
                $"Catalog entry '{report.Id}' defines no '{OverheadParameter}' parameter.");
        var overheadRaw = Value(parameters, OverheadParameter) ?? overheadParam.DefaultValue;
        var overhead = TryNumber(overheadRaw, 0m, MaxOverheadPercent, exclusiveMinimum: false);
        if (overhead is null)
            errors.Add(overheadRaw is null
                ? $"{overheadParam.DisplayName} is required."
                : $"{overheadParam.DisplayName} must be a number from 0 to {MaxOverheadPercent:N0} (got '{overheadRaw}').");

        if (errors.Count > 0) throw new ReportValidationException(Id, errors);
        return (rates, overhead!.Value);
    }

    private static decimal? TryNumber(string? raw, decimal min, decimal max, bool exclusiveMinimum) =>
        raw is not null
        && decimal.TryParse(raw.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
        && (exclusiveMinimum ? n > min : n >= min) && n < max
            ? n
            : null;

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
    IReadOnlyList<BookRate> BookRates,
    decimal MaterialOverheadPercent,
    InventoryValuationOptions Options)
{
    public InventoryValuationRun ToRun() => new(Company, Schema, Warehouses, ItemNumber, ItemGroup,
        BookRates, Options.RateDivision, Options.SpotRateType, MaterialOverheadPercent);
}

/// <summary>What a valuation run was produced with — recorded on the Assumptions sheet.</summary>
public sealed record InventoryValuationRun(
    string Company,
    string Schema,
    IReadOnlyList<string> Warehouses,
    string? ItemNumber,
    string? ItemGroup,
    IReadOnlyList<BookRate> BookRates,
    string RateDivision,
    string SpotRateType,
    decimal MaterialOverheadPercent);
