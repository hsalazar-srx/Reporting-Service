namespace Reporting.Core.Pipeline;

/// <summary>
/// Thrown by ReportPipelineService when the requested format is not supported by the report
/// or when no IRenderer is registered for the format.
/// ReportController maps this to HTTP 400 with UNSUPPORTED_FORMAT code.
/// </summary>
public sealed class ReportFormatException : Exception
{
    public string ReportId { get; }
    public string RequestedFormat { get; }
    public IReadOnlyList<string> SupportedFormats { get; }

    public ReportFormatException(string reportId, string requestedFormat, IReadOnlyList<string> supportedFormats)
        : base($"Format '{requestedFormat}' is not supported by report '{reportId}'. " +
               $"Supported: [{string.Join(", ", supportedFormats)}]")
    {
        ReportId = reportId;
        RequestedFormat = requestedFormat;
        SupportedFormats = supportedFormats;
    }
}
