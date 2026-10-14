# DB2 ODBC Pattern

**Uses skill:** `integration/movex-db2-data-source v1.0`

---

## Critical Rules

1. **Positional parameters only**: `?` not `@` — ODBC requirement
2. **Schema prefix**: `{schema}.{table}` always (e.g., `mvxcdta.MITFAC`)
3. **CHAR padding**: DB2 `CHAR` columns are fixed-width and right-padded — always `.Trim()` string results
4. **Date format**: MOVEX stores dates as `INT YYYYMMDD` — use `MovexDateConverter`
5. **Zero = null**: MOVEX uses `0` for unset dates and quantities

## Connection Setup

```csharp
// DSN-based connection (configured in ODBC Data Source Administrator)
var connectionString = $"DSN={_settings.Dsn};UID={_settings.UserId};PWD={_settings.Password}";
using var connection = new OdbcConnection(connectionString);
await connection.OpenAsync(cancellationToken);
```

## Query Template

Bind parameters explicitly on an `OdbcCommand`, in placeholder order — as `Db2ExchangeRateReader`,
`ItemListFetcher` and `InventoryValuationFetcher` do.

> ⚠️ **Do not pass a positional array to Dapper** (`QueryAsync(cmd)` with `parameters: new[] { … }`).
> Dapper rejects it — *"An enumerable sequence of parameters (arrays, lists, etc) is not allowed in
> this context"*. This template previously recommended exactly that; the three CostManagement
> fetchers followed it and have never run (withdrawn from the catalog 2026-10-01).

```csharp
await using var cmd = new OdbcCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
foreach (var value in parameters)                       // order must match the ? placeholders
    cmd.Parameters.Add("?", OdbcType.VarChar).Value = value;

await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken)) { /* read by ordinal, Trim() strings */ }
```

## Where the SQL Lives

- **Short queries (roughly a screen or less) — inline** in the fetcher as a C# raw string, e.g.
  `ItemListFetcher`.
- **Long or Finance-owned queries — a separate `.sql` file** next to the fetcher, compiled in as an
  `EmbeddedResource` (see `Reporting.Infrastructure.csproj`), e.g.
  `Domains/Finance/InventoryValuation.sql`. It can be diffed against the source query, pasted into a
  DB2 client, and maintained by someone who does not read C#. Substitutions are `{{TOKENS}}` filled by
  the fetcher; mark every deviation from the source query with `[CHANGED]`. Strip full-line `--`
  comments before use, so a `?` or `{{TOKEN}}` written in a comment never counts as a placeholder.

## Schema Switching (Multi-Company)

The schema is **per company** — CONO 100 = `MVXCDTA`, CONO 300 = `MVXC300` — not one schema for
everything. Resolve it per request with `CompanySchemas`:

```csharp
// appsettings.json: "DataSources": { "Db2": {
//     "DefaultCompany": "100",                       // per environment — never in the catalog
//     "CompanySchemas": { "100": "MVXCDTA", "300": "MVXC300" } } }
var (company, schema) = _companySchemas.Resolve(parameters["companyCode"]);
var sql = template.Replace("{schema}", schema);       // validated identifier — safe to inline
```

`CompanySchemas` validates schemas as plain identifiers and company codes as digits only, so both can
be written into SQL text. Everything user-supplied still goes through `?`.

## Polly Policy (must implement from scratch — NOT in DirectQueryDataSource template)

```csharp
_pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        ShouldHandle = new PredicateBuilder()
            .Handle<OdbcException>()
            .Handle<TimeoutException>()
            .Handle<OperationCanceledException>(ex => !cancellationToken.IsCancellationRequested)
    })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 5,
        SamplingDuration = TimeSpan.FromSeconds(60),
        BreakDuration = TimeSpan.FromSeconds(30)
    })
    .Build();
```

## MovexDateConverter

```csharp
// INT 20260101 → DateTime(2026, 1, 1)
public static DateTime? FromMovexInt(int movexDate)
{
    if (movexDate <= 0) return null;
    return DateTime.ParseExact(movexDate.ToString("D8"), "yyyyMMdd", CultureInfo.InvariantCulture);
}

// DateTime(2026, 1, 1) → INT 20260101
public static int ToMovexInt(DateTime date) =>
    int.Parse(date.ToString("yyyyMMdd"));
```

## Common Pitfalls

| Pitfall | Symptom | Fix |
|---|---|---|
| Using `@param` in SQL | OdbcException: syntax error | Change to `?` |
| Missing schema prefix | OdbcException: table not found | Add `mvxcdta.` prefix |
| Not trimming CHAR results | "ITEM001   " != "ITEM001" | `.Trim()` all string columns |
| Assuming Polly is in template | No retry on DB2 drop | Implement Polly from scratch |
| Using MITBAL in Cost Management | Domain boundary violation | Use only in Iteration 4 |
