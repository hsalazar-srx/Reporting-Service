namespace Reporting.Core.Pipeline;

/// <summary>
/// The final rendered output from IRenderer — bytes plus content metadata.
/// Returned by ReportPipelineService and streamed back through ReportController.
///
/// Uses skill: data/report-generation v1.0
/// </summary>
public sealed record ReportOutput
{
    /// <summary>Rendered bytes — JSON UTF-8, Excel .xlsx, or PDF binary.</summary>
    public required byte[] Content { get; init; }

    /// <summary>MIME type: application/json | application/vnd.openxmlformats-officedocument.spreadsheetml.sheet | application/pdf</summary>
    public required string ContentType { get; init; }

    /// <summary>Suggested file name for Content-Disposition header (e.g., "average-cost-snapshot-2026-04-15.xlsx").</summary>
    public required string FileName { get; init; }

    /// <summary>Format token used to produce this output: json | excel | pdf.</summary>
    public required string Format { get; init; }
}
