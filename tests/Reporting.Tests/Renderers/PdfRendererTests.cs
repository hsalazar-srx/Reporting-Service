using FluentAssertions;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Renderers;

namespace Reporting.Tests.Renderers;

public sealed class PdfRendererTests
{
    private static readonly byte[] PdfMagicBytes = "%PDF"u8.ToArray();

    private static ReportDefinition MakeReport() => new()
    {
        Id = "cost.test",
        DisplayName = "Test Report",
        Domain = "CostManagement",
        DataSource = "Db2Direct",
        SupportedFormats = ["pdf"],
        DefaultFormat = "pdf"
    };

    private static ReportDataSet MakeDataSet(int rowCount = 2) => new()
    {
        ReportId = "cost.test",
        ExecutedAtUtc = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc),
        Columns = ["Facility", "ItemNumber", "WacCost"],
        Rows = Enumerable.Range(1, rowCount).Select(i =>
            (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Facility"] = "001",
                ["ItemNumber"] = $"ITEM{i:D4}",
                ["WacCost"] = (decimal)(i * 10.5m)
            }).ToList().AsReadOnly()
    };

    [Fact]
    public async Task RenderAsync_ValidDataSet_ReturnsPdfBytes()
    {
        var renderer = new PdfRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        output.Content.Should().NotBeEmpty();
        output.ContentType.Should().Be("application/pdf");
        output.Format.Should().Be("pdf");
        output.FileName.Should().EndWith(".pdf");
    }

    [Fact]
    public async Task RenderAsync_OutputStartsWithPdfMagicBytes()
    {
        var renderer = new PdfRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        output.Content.Take(4).Should().Equal(PdfMagicBytes);
    }

    [Fact]
    public async Task RenderAsync_EmptyDataSet_StillProducesValidPdf()
    {
        var emptyDataSet = new ReportDataSet
        {
            ReportId = "cost.test",
            ExecutedAtUtc = DateTime.UtcNow,
            Columns = ["Facility", "ItemNumber"],
            Rows = []
        };

        var renderer = new PdfRenderer();
        var output = await renderer.RenderAsync(emptyDataSet, MakeReport());

        output.Content.Should().NotBeEmpty();
        output.Content.Take(4).Should().Equal(PdfMagicBytes);
    }

    [Fact]
    public async Task RenderAsync_FileNameContainsReportIdAndDate()
    {
        var renderer = new PdfRenderer();
        var output = await renderer.RenderAsync(MakeDataSet(), MakeReport());

        output.FileName.Should().Contain("cost.test");
        output.FileName.Should().Contain("2026-04-15");
    }

    [Fact]
    public async Task RenderAsync_WithSubReports_ProducesLargerPdf()
    {
        var withoutSub = MakeDataSet();
        var withSub = MakeDataSet() with
        {
            SubReports =
            [
                new ReportDataSet
                {
                    ReportId = "cost.test.sub",
                    SectionLabel = "Sub Section",
                    ExecutedAtUtc = DateTime.UtcNow,
                    Columns = ["Col1", "Col2"],
                    Rows = [new Dictionary<string, object?> { ["Col1"] = "A", ["Col2"] = "B" }]
                }
            ]
        };

        var renderer = new PdfRenderer();
        var outputWithout = await renderer.RenderAsync(withoutSub, MakeReport());
        var outputWith = await renderer.RenderAsync(withSub, MakeReport());

        outputWith.Content.Length.Should().BeGreaterThan(outputWithout.Content.Length);
    }
}
