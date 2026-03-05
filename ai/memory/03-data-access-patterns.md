# Data Access Patterns — Reporting Service

**Version:** 1.0
**Date:** March 2026

---

## DB2 ODBC Pattern (Db2DirectFetcher)

### Critical Rules
- ⚠️ Parameters are **positional** (`?` not `@`) — ODBC requirement
- ⚠️ Schema must be prefixed: `{schema}.{table}` (e.g., `mvxcdta.MITFAC`)
- ⚠️ String types in DB2 are often `CHAR` (fixed-width, right-padded with spaces) — always `.Trim()` results
- Use Dapper `QueryAsync<T>` with `CommandDefinition` for timeout + cancellation

### Dapper CommandDefinition Template

```csharp
var cmd = new CommandDefinition(
    commandText: sql,
    parameters: new { /* anonymous or DynamicParameters */ },
    commandTimeout: _commandTimeout,
    cancellationToken: cancellationToken);

var results = await connection.QueryAsync<T>(cmd);
```

### ODBC Positional Parameters

```sql
-- WRONG: named params don't work in ODBC
SELECT * FROM mvxcdta.MITFAC WHERE CFFACI = @facility

-- CORRECT: positional only
SELECT * FROM mvxcdta.MITFAC WHERE CFFACI = ?
```

When using Dapper with ODBC and positional params, pass parameters as an array or anonymous type
in the exact order they appear in the SQL.

### Polly Configuration (Db2DirectFetcher)

```csharp
// Retry: 3 attempts, exponential backoff (1s, 2s, 4s)
// CircuitBreaker: 5 failures → 30s open
ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        ShouldHandle = new PredicateBuilder()
            .Handle<OdbcException>()
            .Handle<TimeoutException>()
    })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        MinimumThroughput = 5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(30)
    })
    .Build();
```

### MOVEX Date Conversion

MOVEX stores dates as `INT` in `YYYYMMDD` format:
- `20260101` = 2026-01-01
- `0` = null/not set

```csharp
// MovexDateConverter.cs
public static DateTime? FromMovexInt(int movexDate)
{
    if (movexDate <= 0) return null;
    var str = movexDate.ToString("D8");
    return DateTime.ParseExact(str, "yyyyMMdd", CultureInfo.InvariantCulture);
}

public static int ToMovexInt(DateTime date) =>
    int.Parse(date.ToString("yyyyMMdd"));
```

---

## SQL Server Pattern (MovexDwFetcher)

### Key Differences from Db2DirectFetcher
- Named parameters: `@facility` (standard SqlClient)
- No schema prefix needed (DW uses default schema)
- Connection string: `MovexDatawarehouse` (150.3.20.116)

### Example

```csharp
var cmd = new CommandDefinition(
    commandText: @"
        SELECT CustomerCode, OrderDate, DeliveryDate, IsOnTime, IsInFull
        FROM DIFOT_Monthly
        WHERE FacilityCode = @facility AND PeriodDate BETWEEN @from AND @to",
    parameters: new { facility, from = dateFrom, to = dateTo },
    commandTimeout: _commandTimeout,
    cancellationToken: cancellationToken);
```

---

## ReportDataSet Structure

```csharp
public class ReportDataSet
{
    public string ReportId { get; init; }
    public DateTime ExecutedAtUtc { get; init; }    // Always stamped at fetch start
    public IReadOnlyList<string> Columns { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
    public IReadOnlyList<ReportDataSet> SubReports { get; init; }  // Smartbook sections
}
```

`ExecutedAtUtc` is stamped at the moment `ReportPipelineService` begins the fetch stage.
It appears in:
- PDF: page 1 header ("Data as at: 2026-03-04 14:32:00 UTC")
- Excel: header row of each worksheet
- JSON: top-level field in response

SubReports map to:
- PDF: additional page sections
- Excel: additional worksheets
- JSON: nested array
