namespace Reporting.Core.Pipeline;

/// <summary>
/// Thrown by ReportPipelineService when parameter validation fails.
/// ReportController maps this to HTTP 400 with PARAMETER_VALIDATION_FAILED code.
/// </summary>
public sealed class ReportValidationException : Exception
{
    public string ReportId { get; }
    public IReadOnlyList<string> Errors { get; }

    public ReportValidationException(string reportId, IReadOnlyList<string> errors)
        : base($"Parameter validation failed for report '{reportId}': {string.Join("; ", errors)}")
    {
        ReportId = reportId;
        Errors = errors;
    }
}
