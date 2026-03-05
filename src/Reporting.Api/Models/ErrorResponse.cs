namespace Reporting.Api.Models;

/// <summary>
/// Standardized error response returned by all API error paths.
/// SM-Portal maps error codes to user-friendly messages.
///
/// Gap 10 mitigation: consistent error contract across all endpoints.
/// </summary>
/// <param name="Code">
/// One of: REPORT_NOT_FOUND | PARAMETER_VALIDATION_FAILED |
/// DATA_SOURCE_UNAVAILABLE | EXECUTION_TIMEOUT | UNAUTHORIZED
/// </param>
/// <param name="Message">Human-readable description of the error.</param>
/// <param name="CorrelationId">Request trace ID for support lookups.</param>
/// <param name="Timestamp">UTC timestamp of the error.</param>
public sealed record ErrorResponse(
    string Code,
    string Message,
    string CorrelationId,
    DateTime Timestamp);
