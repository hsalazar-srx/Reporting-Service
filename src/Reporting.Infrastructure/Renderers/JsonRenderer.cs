using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Renderers;

/// <summary>
/// Renders a ReportDataSet to JSON bytes.
/// SubReports are serialised as nested arrays under "subReports".
/// Used for /preview (always) and /execute when format=json.
///
/// Uses skill: data/report-generation v1.0
/// </summary>
public sealed class JsonRenderer : IRenderer
{
    public string Format => "json";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public Task<ReportOutput> RenderAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(dataSet);
        var json = JsonSerializer.Serialize(payload, _options);
        var bytes = Encoding.UTF8.GetBytes(json);

        var fileName = $"{report.Id}-{dataSet.ExecutedAtUtc:yyyy-MM-dd}.json";

        return Task.FromResult(new ReportOutput
        {
            Content = bytes,
            ContentType = "application/json",
            FileName = fileName,
            Format = "json"
        });
    }

    private static object BuildPayload(ReportDataSet dataSet)
    {
        return new
        {
            reportId = dataSet.ReportId,
            executedAtUtc = dataSet.ExecutedAtUtc,
            rowCount = dataSet.Rows.Count,
            columns = dataSet.Columns,
            rows = dataSet.Rows,
            subReports = dataSet.SubReports.Select(sr => new
            {
                sectionLabel = sr.SectionLabel,
                reportId = sr.ReportId,
                rowCount = sr.Rows.Count,
                columns = sr.Columns,
                rows = sr.Rows
            }).ToList()
        };
    }
}
