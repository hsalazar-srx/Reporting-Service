namespace Reporting.Api.Models;

/// <summary>
/// Request body for POST /api/v1/reports/{id}/execute and /preview.
/// </summary>
public sealed record ReportExecuteRequest
{
    /// <summary>Output format: json | excel | pdf. Defaults to report's defaultFormat.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// Report parameters as key-value pairs.
    /// Keys must match parameter names defined in report-catalog.json.
    /// </summary>
    public Dictionary<string, string> Parameters { get; init; } = [];
}
