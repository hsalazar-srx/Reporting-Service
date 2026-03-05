# Report Pipeline Pattern

**Uses skill:** `data/report-generation v1.0`

---

## Pipeline Stages

```
1. Catalog Lookup     → IReportCatalogProvider.GetReportAsync(id)
2. Parameter Validate → IParameterValidator.ValidateAsync(request, definition)
3. Data Fetch         → IDataFetcher.FetchAsync(request, definition, cancellationToken)
4. Transform          → ITransformer.TransformAsync(dataSet, definition)
5. Render             → IRenderer.RenderAsync(dataSet, format)
6. HTTP Response      → with Content-Disposition + X-Api-Version headers
```

## Interface Contracts

```csharp
// Core interfaces (Reporting.Core/Pipeline/)

public interface IDataFetcher
{
    Task<ReportDataSet> FetchAsync(
        ReportRequest request,
        ReportDefinition definition,
        CancellationToken cancellationToken);
}

public interface ITransformer
{
    Task<ReportDataSet> TransformAsync(
        ReportDataSet raw,
        ReportDefinition definition);
}

public interface IRenderer
{
    Task<ReportOutput> RenderAsync(ReportDataSet data, ReportFormat format);
}
```

## ReportDataSet

```csharp
public sealed record ReportDataSet
{
    public required string ReportId { get; init; }
    public required DateTime ExecutedAtUtc { get; init; }    // Stamped at fetch start
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
    public IReadOnlyList<ReportDataSet> SubReports { get; init; } = [];  // Smartbook sections
}
```

## Error Handling in Pipeline

- **Catalog not found** → `REPORT_NOT_FOUND` (404)
- **Parameter invalid** → `PARAMETER_VALIDATION_FAILED` (400) with field-level detail
- **DB source down** → `DATA_SOURCE_UNAVAILABLE` (503) after Polly exhausted
- **Timeout** → `EXECUTION_TIMEOUT` (408) after `executionTimeoutSeconds`
- **Unknown** → 500 with correlationId for support

## Smartbook Pattern

SubReports execute sequentially (not parallel — shared ODBC connection):

```csharp
var mainData = await fetcher.FetchAsync(request, definition, ct);
var subReport1 = await fetcher.FetchAsync(subRequest1, subDef1, ct);
var subReport2 = await fetcher.FetchAsync(subRequest2, subDef2, ct);

var fullDataSet = mainData with
{
    SubReports = [subReport1, subReport2]
};
```
