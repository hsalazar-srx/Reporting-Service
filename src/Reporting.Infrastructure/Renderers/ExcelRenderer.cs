using ClosedXML.Excel;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Renderers;

/// <summary>
/// Renders a ReportDataSet to an Excel .xlsx workbook using ClosedXML (MIT).
///
/// Conventions (skill: data/report-generation v1.1):
///   - Row 1: report title, as-at date, run timestamp (merged cells)
///   - Row 2: column headers (bold, freeze pane below this row)
///   - Row 3+: data rows
///   - Last row: SUM formula for all decimal/numeric columns
///   - Auto-filter on header row
///   - One worksheet per SubReport; main dataset on first sheet
///
/// Uses skill: data/report-generation v1.1
/// </summary>
public sealed class ExcelRenderer : IRenderer
{
    public string Format => "excel";

    public Task<ReportOutput> RenderAsync(
        ReportDataSet dataSet,
        ReportDefinition report,
        CancellationToken cancellationToken = default)
    {
        using var workbook = new XLWorkbook();

        AddWorksheet(workbook, report.DisplayName, dataSet);

        foreach (var sub in dataSet.SubReports)
        {
            var sheetName = SanitizeSheetName(
                string.IsNullOrWhiteSpace(sub.SectionLabel) ? sub.ReportId : sub.SectionLabel);
            AddWorksheet(workbook, sheetName, sub);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var bytes = stream.ToArray();

        var fileName = $"{report.Id}-{dataSet.ExecutedAtUtc:yyyy-MM-dd}.xlsx";

        return Task.FromResult(new ReportOutput
        {
            Content = bytes,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileName = fileName,
            Format = "excel"
        });
    }

    private static void AddWorksheet(IXLWorkbook workbook, string sheetName, ReportDataSet dataSet)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        if (dataSet.Columns.Count == 0)
            return;

        var colCount = dataSet.Columns.Count;

        // Row 1 — report header
        ws.Cell(1, 1).Value = sheetName;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 12;
        ws.Range(1, 1, 1, colCount).Merge();

        ws.Cell(1, colCount).Value = $"Data as at: {dataSet.ExecutedAtUtc:yyyy-MM-dd HH:mm} UTC";
        ws.Cell(1, colCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        // Row 2 — column headers
        for (var c = 0; c < colCount; c++)
        {
            var cell = ws.Cell(2, c + 1);
            cell.Value = dataSet.Columns[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // Freeze pane below header row
        ws.SheetView.FreezeRows(2);

        if (dataSet.Rows.Count == 0)
        {
            ws.Columns().AdjustToContents();
            return;
        }

        // Data rows
        for (var r = 0; r < dataSet.Rows.Count; r++)
        {
            var row = dataSet.Rows[r];
            for (var c = 0; c < colCount; c++)
            {
                var colName = dataSet.Columns[c];
                row.TryGetValue(colName, out var val);
                SetCellValue(ws.Cell(r + 3, c + 1), val);
            }
        }

        // Auto-filter on header row
        ws.RangeUsed()!.SetAutoFilter();

        // SUM row for numeric columns
        var dataEndRow = dataSet.Rows.Count + 2;
        var sumRow = dataEndRow + 1;
        var hasSums = false;

        for (var c = 0; c < colCount; c++)
        {
            if (IsNumericColumn(dataSet, c))
            {
                var col = c + 1;
                ws.Cell(sumRow, col).FormulaA1 =
                    $"=SUM({ws.Cell(3, col).Address}:{ws.Cell(dataEndRow, col).Address})";
                ws.Cell(sumRow, col).Style.Font.Bold = true;
                ws.Cell(sumRow, col).Style.NumberFormat.Format = "#,##0.0000";
                hasSums = true;
            }
        }

        if (hasSums)
        {
            ws.Cell(sumRow, 1).Value = "TOTAL";
            ws.Cell(sumRow, 1).Style.Font.Bold = true;
        }

        ws.Columns().AdjustToContents();
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case decimal d:
                cell.Value = d;
                cell.Style.NumberFormat.Format = "#,##0.0000";
                break;
            case double dbl:
                cell.Value = dbl;
                cell.Style.NumberFormat.Format = "#,##0.0000";
                break;
            case float f:
                cell.Value = f;
                cell.Style.NumberFormat.Format = "#,##0.0000";
                break;
            case int i:
                cell.Value = i;
                break;
            case long l:
                cell.Value = l;
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = "yyyy-MM-dd";
                break;
            case bool b:
                cell.Value = b;
                break;
            default:
                cell.Value = value.ToString();
                break;
        }
    }

    private static bool IsNumericColumn(ReportDataSet dataSet, int colIndex)
    {
        var colName = dataSet.Columns[colIndex];
        foreach (var row in dataSet.Rows)
        {
            if (!row.TryGetValue(colName, out var val) || val is null)
                continue;
            return val is decimal or double or float;
        }
        return false;
    }

    private static string SanitizeSheetName(string name)
    {
        // Excel sheet names: max 31 chars, no \ / ? * [ ]
        var invalid = new[] { '\\', '/', '?', '*', '[', ']', ':' };
        foreach (var c in invalid)
            name = name.Replace(c, '_');
        return name.Length > 31 ? name[..31] : name;
    }
}
