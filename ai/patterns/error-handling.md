# Error Handling Pattern

---

## ErrorResponse Schema

```json
{
  "code": "REPORT_NOT_FOUND",
  "message": "Report 'cost.xyz' is not registered in the catalog.",
  "correlationId": "12345678-1234-1234-1234-123456789abc",
  "timestamp": "2026-03-04T10:00:00Z"
}
```

## Error Codes

| Code | HTTP Status | When |
|---|---|---|
| `REPORT_NOT_FOUND` | 404 | Report ID not in catalog |
| `PARAMETER_VALIDATION_FAILED` | 400 | Missing/invalid parameter; include field-level detail in message |
| `DATA_SOURCE_UNAVAILABLE` | 503 | DB2 circuit open or SQL Server unavailable after Polly exhausted |
| `EXECUTION_TIMEOUT` | 408 | Report exceeded `executionTimeoutSeconds` |
| `UNAUTHORIZED` | 401 | Missing or invalid API key |

## C# ErrorResponse Record

```csharp
// Reporting.Api/Models/ErrorResponse.cs
public sealed record ErrorResponse(
    string Code,
    string Message,
    string CorrelationId,
    DateTime Timestamp);
```

## Controller Pattern

```csharp
// Always include correlationId
var correlationId = HttpContext.TraceIdentifier;

return problem is ReportNotFoundException
    ? NotFound(new ErrorResponse("REPORT_NOT_FOUND", problem.Message, correlationId, DateTime.UtcNow))
    : StatusCode(503, new ErrorResponse("DATA_SOURCE_UNAVAILABLE", ..., correlationId, DateTime.UtcNow));
```

## SM-Portal Mapping

SM-Portal's `ReportingController` proxy maps error codes to user-friendly messages:

| Code | User Message |
|---|---|
| `DATA_SOURCE_UNAVAILABLE` | "Report system temporarily unavailable. Please try again in a few minutes." |
| `EXECUTION_TIMEOUT` | "Report took too long to generate. Try a smaller date range." |
| `PARAMETER_VALIDATION_FAILED` | Show field-level validation errors from the `message` field |
| `REPORT_NOT_FOUND` | "The requested report is no longer available." |
