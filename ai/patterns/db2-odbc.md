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

```csharp
// All parameters positional — order matters
var sql = $@"
    SELECT
        f.CFFACI,
        f.CFITNO,
        m.MMITDS,
        f.CFUCOS AS WacCost,
        f.CFACSO AS StandardCost
    FROM {schema}.MITFAC f
    JOIN {schema}.MITMAS m ON m.MMITNO = f.CFITNO
    WHERE f.CFFACI = ?
      AND m.MMSTAT = ?
    ORDER BY f.CFFACI, f.CFITNO";

var cmd = new CommandDefinition(
    commandText: sql,
    parameters: new[] { facility, "20" },   // positional array
    commandTimeout: timeoutSeconds,
    cancellationToken: cancellationToken);

var results = await connection.QueryAsync<CostRecord>(cmd);
```

## Schema Switching (Multi-Company)

```csharp
// appsettings.json:
// "DataSources": { "Db2": { "Schema": "mvxcdta" } }

// Inject schema into SQL at build time — not via SQL SET SCHEMA
var sql = sql.Replace("{schema}", _settings.Schema);
```

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
