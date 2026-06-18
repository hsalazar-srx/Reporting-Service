using System.Data;
using FastReport;
using FastReport.Export.PdfSimple;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Renderers;

/// <summary>
/// Renders a ReportDataSet to PDF using FastReport Open Source (MIT).
///
/// Layout is built programmatically from ReportDataSet — no .frx template file required.
/// SubReports are rendered as additional pages with a section heading.
///
/// Conventions (skill: data/report-generation v1.1):
///   - A4 landscape
///   - Page 1 header: report title + "Data as at: ..." timestamp
///   - Column headers: bold, dark blue background
///   - Alternating row shading for readability
///   - Page footer: page number + run date
///   - One section per SubReport, each beginning on a new page
///
/// Uses skill: data/report-generation v1.1
/// </summary>
public sealed class PdfRenderer : IRenderer
{
    public string Format => "pdf";

    // A4 landscape in FastReport units (1 unit = 1/96 inch converted to mm: 96 units = 25.4mm)
    // FastReport uses internal units (1 unit = 1/96 inch ≈ 0.2646mm)
    // A4 landscape: 297mm × 210mm = 1122.5 × 793.7 internal units
    private const float PageWidth = 1122.5f;
    private const float PageHeight = 793.7f;
    private const float Margin = 37.8f;   // ~10mm margins
    private const float HeaderHeight = 56.7f;  // ~15mm
    private const float FooterHeight = 37.8f;  // ~10mm
    private const float RowHeight = 18.9f;     // ~5mm
    private const float ColHeaderHeight = 22.7f;

    public Task<ReportOutput> RenderAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        CancellationToken cancellationToken = default)
    {
        var dt = BuildDataTable(dataSet);

        using var frReport = new Report();
        frReport.FileName = report.Id;

        // Register data source
        frReport.RegisterData(dt, "ReportData");

        var page = CreatePage(frReport);

        // Page header band
        var header = CreatePageHeader(frReport, report.DisplayName, dataSet.ExecutedAtUtc);
        page.AddChild(header);

        // Column headers band
        if (dataSet.Columns.Count > 0)
        {
            var colHeader = CreateColumnHeader(frReport, dataSet.Columns, page);
            page.AddChild(colHeader);
        }

        // Data band
        var dataBand = CreateDataBand(frReport, dataSet.Columns, page);
        page.AddChild(dataBand);

        // SubReport sections — each starts on a new page via StartNewPage on the title band
        foreach (var sub in dataSet.SubReports)
        {
            var subDt = BuildDataTable(sub);
            var subDsName = $"SubReport_{sub.ReportId.Replace('.', '_')}";
            frReport.RegisterData(subDt, subDsName);

            var subTitle = CreateSectionTitle(frReport,
                string.IsNullOrWhiteSpace(sub.SectionLabel) ? sub.ReportId : sub.SectionLabel,
                startNewPage: true);
            page.AddChild(subTitle);

            if (sub.Columns.Count > 0)
            {
                var subColHeader = CreateColumnHeader(frReport, sub.Columns, page);
                page.AddChild(subColHeader);
            }

            var subDataBand = CreateDataBand(frReport, sub.Columns, page, subDsName);
            page.AddChild(subDataBand);
        }

        // Page footer
        var footer = CreatePageFooter(frReport, dataSet.ExecutedAtUtc);
        page.AddChild(footer);

        frReport.Prepare();

        using var stream = new MemoryStream();
        var export = new PDFSimpleExport();
        frReport.Export(export, stream);
        var bytes = stream.ToArray();

        var fileName = $"{report.Id}-{dataSet.ExecutedAtUtc:yyyy-MM-dd}.pdf";

        return Task.FromResult(new ReportOutput
        {
            Content = bytes,
            ContentType = "application/pdf",
            FileName = fileName,
            Format = "pdf"
        });
    }

    private static ReportPage CreatePage(Report report)
    {
        var page = new ReportPage
        {
            Name = "Page1",
            PaperWidth = PageWidth / 3.779527f,   // convert to mm
            PaperHeight = PageHeight / 3.779527f,
            Landscape = true,
            LeftMargin = Margin / 3.779527f,
            RightMargin = Margin / 3.779527f,
            TopMargin = Margin / 3.779527f,
            BottomMargin = Margin / 3.779527f
        };
        report.Pages.Add(page);
        return page;
    }

    private static PageHeaderBand CreatePageHeader(Report report, string title, DateTime executedAtUtc)
    {
        var band = new PageHeaderBand
        {
            Name = "PageHeader1",
            Height = HeaderHeight
        };

        var titleText = new TextObject
        {
            Name = "TitleText",
            Text = title,
            Left = 0,
            Top = 0,
            Width = PageWidth - Margin * 2,
            Height = HeaderHeight / 2,
            Font = new System.Drawing.Font("Arial", 14, System.Drawing.FontStyle.Bold),
            HorzAlign = HorzAlign.Left
        };

        var dateText = new TextObject
        {
            Name = "DateText",
            Text = $"Data as at: {executedAtUtc:yyyy-MM-dd HH:mm} UTC",
            Left = 0,
            Top = HeaderHeight / 2,
            Width = PageWidth - Margin * 2,
            Height = HeaderHeight / 2,
            Font = new System.Drawing.Font("Arial", 9),
            HorzAlign = HorzAlign.Left,
            TextColor = System.Drawing.Color.Gray
        };

        band.AddChild(titleText);
        band.AddChild(dateText);
        return band;
    }

    private static ColumnHeaderBand CreateColumnHeader(Report report, IReadOnlyList<string> columns, ReportPage page)
    {
        var band = new ColumnHeaderBand
        {
            Name = $"ColHeader_{Guid.NewGuid():N}",
            Height = ColHeaderHeight
        };

        var usableWidth = PageWidth - Margin * 2;
        var colWidth = usableWidth / columns.Count;

        for (var i = 0; i < columns.Count; i++)
        {
            var cell = new TextObject
            {
                Name = $"ColHdr_{i}_{Guid.NewGuid():N}",
                Text = columns[i],
                Left = i * colWidth,
                Top = 0,
                Width = colWidth,
                Height = ColHeaderHeight,
                Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold),
                HorzAlign = HorzAlign.Center,
                VertAlign = VertAlign.Center,
                FillColor = System.Drawing.Color.FromArgb(31, 78, 121),
                TextColor = System.Drawing.Color.White,
                Border = new Border { Lines = BorderLines.All, Color = System.Drawing.Color.White }
            };
            band.AddChild(cell);
        }

        return band;
    }

    private static DataBand CreateDataBand(Report report, IReadOnlyList<string> columns, ReportPage page, string? dataSourceName = null)
    {
        var band = new DataBand
        {
            Name = $"DataBand_{Guid.NewGuid():N}",
            Height = RowHeight,
            EvenStyle = "EvenRows"
        };

        if (dataSourceName != null)
            band.DataSource = report.GetDataSource(dataSourceName) as FastReport.Data.TableDataSource;
        else
            band.DataSource = report.GetDataSource("ReportData") as FastReport.Data.TableDataSource;

        if (columns.Count == 0)
            return band;

        var usableWidth = PageWidth - Margin * 2;
        var colWidth = usableWidth / columns.Count;

        for (var i = 0; i < columns.Count; i++)
        {
            var colName = columns[i];
            var cell = new TextObject
            {
                Name = $"DataCell_{i}_{Guid.NewGuid():N}",
                Text = $"[{(dataSourceName ?? "ReportData")}.{colName}]",
                Left = i * colWidth,
                Top = 0,
                Width = colWidth,
                Height = RowHeight,
                Font = new System.Drawing.Font("Arial", 8),
                VertAlign = VertAlign.Center,
                Border = new Border
                {
                    Lines = BorderLines.Bottom,
                    Color = System.Drawing.Color.LightGray
                }
            };
            band.AddChild(cell);
        }

        return band;
    }

    private static BandBase CreateSectionTitle(Report report, string title, bool startNewPage = false)
    {
        var band = new GroupHeaderBand
        {
            Name = $"SectionTitle_{Guid.NewGuid():N}",
            Height = HeaderHeight / 2,
            StartNewPage = startNewPage
        };

        var text = new TextObject
        {
            Name = $"SectionTitleText_{Guid.NewGuid():N}",
            Text = title,
            Left = 0,
            Top = 0,
            Width = PageWidth - Margin * 2,
            Height = HeaderHeight / 2,
            Font = new System.Drawing.Font("Arial", 11, System.Drawing.FontStyle.Bold),
            HorzAlign = HorzAlign.Left
        };
        band.AddChild(text);
        return band;
    }

    private static PageFooterBand CreatePageFooter(Report report, DateTime executedAtUtc)
    {
        var band = new PageFooterBand
        {
            Name = "PageFooter1",
            Height = FooterHeight
        };

        var pageNum = new TextObject
        {
            Name = "PageNum",
            Text = "Page [Page#] of [TotalPages#]",
            Left = 0,
            Top = 0,
            Width = (PageWidth - Margin * 2) / 2,
            Height = FooterHeight,
            Font = new System.Drawing.Font("Arial", 8),
            HorzAlign = HorzAlign.Left,
            TextColor = System.Drawing.Color.Gray
        };

        var runDate = new TextObject
        {
            Name = "RunDate",
            Text = $"Generated: {executedAtUtc:yyyy-MM-dd HH:mm} UTC",
            Left = (PageWidth - Margin * 2) / 2,
            Top = 0,
            Width = (PageWidth - Margin * 2) / 2,
            Height = FooterHeight,
            Font = new System.Drawing.Font("Arial", 8),
            HorzAlign = HorzAlign.Right,
            TextColor = System.Drawing.Color.Gray
        };

        band.AddChild(pageNum);
        band.AddChild(runDate);
        return band;
    }

    private static DataTable BuildDataTable(ReportDataSet dataSet)
    {
        var dt = new DataTable("ReportData");

        foreach (var col in dataSet.Columns)
            dt.Columns.Add(col, typeof(string));

        foreach (var row in dataSet.Rows)
        {
            var dr = dt.NewRow();
            foreach (var col in dataSet.Columns)
            {
                row.TryGetValue(col, out var val);
                dr[col] = val?.ToString() ?? string.Empty;
            }
            dt.Rows.Add(dr);
        }

        return dt;
    }
}
