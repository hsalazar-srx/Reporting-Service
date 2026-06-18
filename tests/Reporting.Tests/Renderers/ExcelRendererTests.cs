using ClosedXML.Excel;
using FluentAssertions;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Renderers;

namespace Reporting.Tests.Renderers;

public sealed class ExcelRendererTests
{
    private static ReportDefinition MakeReport() => new()
    {
        Id = "cost.test",
        DisplayName = "Test Report",
        Domain = "CostManagement",
        DataSource = "Db2Direct",
        SupportedFormats = ["excel"],
        DefaultFormat = "excel"
    };

    private static ReportDataSet MakeDataSet(int rowCount = 2) => new()
    {
        ReportId = "cost.test",
        ExecutedAtUtc = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc),
        Columns = ["Facility", "ItemNumber", "WacCost", "StandardCost"],
        Rows = Enumerable.Range(1, rowCount).Select(i =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Facility"] = "001",
                ["ItemNumber"] = $"ITEM{i:D4}",
                ["WacCost"] = (decimal)(i * 10.5m),
                ["StandardCost"] = (decimal)(i * 10.0m)
            }).ToList().AsReadOnly()
    };

    private static XLWorkbook OpenWorkbook(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new XLWorkbook(stream);
    }

    [Fact]
    public async Task RenderAsync_ValidDataSet_ReturnsXlsxBytes()
    {
        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        output.Content.Should().NotBeEmpty();
        output.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        output.Format.Should().Be("excel");
        output.FileName.Should().EndWith(".xlsx");
    }

    [Fact]
    public async Task RenderAsync_OutputIsValidXlsx()
    {
        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        var act = () => OpenWorkbook(output.Content);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task RenderAsync_HeaderRowIsBold()
    {
        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        using var wb = OpenWorkbook(output.Content);
        var ws = wb.Worksheets.First();

        // Row 2 = column headers
        ws.Cell(2, 1).Style.Font.Bold.Should().BeTrue();
        ws.Cell(2, 2).Style.Font.Bold.Should().BeTrue();
    }

    [Fact]
    public async Task RenderAsync_DataRowsPresent()
    {
        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(3), MakeReport());

        using var wb = OpenWorkbook(output.Content);
        var ws = wb.Worksheets.First();

        // Row 1 = title, Row 2 = headers, Rows 3-5 = data
        ws.Cell(3, 2).GetString().Should().Be("ITEM0001");
        ws.Cell(4, 2).GetString().Should().Be("ITEM0002");
        ws.Cell(5, 2).GetString().Should().Be("ITEM0003");
    }

    [Fact]
    public async Task RenderAsync_ColumnHeadersMatchDataSet()
    {
        var renderer = new ExcelRenderer();
        var dataSet = MakeDataSet();
        var output = await renderer.RenderAsync(dataSet, MakeReport());

        using var wb = OpenWorkbook(output.Content);
        var ws = wb.Worksheets.First();

        for (var i = 0; i < dataSet.Columns.Count; i++)
            ws.Cell(2, i + 1).GetString().Should().Be(dataSet.Columns[i]);
    }

    [Fact]
    public async Task RenderAsync_EmptyDataSet_StillProducesValidFile()
    {
        var emptyDataSet = new ReportDataSet
        {
            ReportId = "cost.test",
            ExecutedAtUtc = DateTime.UtcNow,
            Columns = ["Facility", "ItemNumber"],
            Rows = []
        };

        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(emptyDataSet, MakeReport());

        output.Content.Should().NotBeEmpty();
        var act = () => OpenWorkbook(output.Content);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task RenderAsync_SubReport_CreatesAdditionalWorksheet()
    {
        var dataSetWithSub = MakeDataSet() with
        {
            SubReports =
            [
                new ReportDataSet
                {
                    ReportId = "cost.test.sub",
                    SectionLabel = "Sub Section",
                    ExecutedAtUtc = DateTime.UtcNow,
                    Columns = ["Col1"],
                    Rows = [new Dictionary<string, object?> { ["Col1"] = "val" }]
                }
            ]
        };

        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(dataSetWithSub, MakeReport());

        using var wb = OpenWorkbook(output.Content);
        wb.Worksheets.Count.Should().Be(2);
        wb.Worksheets.Last().Name.Should().Be("Sub Section");
    }

    [Fact]
    public async Task RenderAsync_FileNameContainsReportIdAndDate()
    {
        var renderer = new ExcelRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        output.FileName.Should().Contain("cost.test");
        output.FileName.Should().Contain("2026-04-15");
    }
}
