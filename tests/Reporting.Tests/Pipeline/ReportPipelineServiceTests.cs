using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Tests.Pipeline;

public sealed class ReportPipelineServiceTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static ReportDefinition MakeReport(string id = "cost.test", string format = "json") => new()
    {
        Id = id,
        DisplayName = "Test Report",
        Domain = "CostManagement",
        DataSource = "Db2Direct",
        SupportedFormats = [format, "excel"],
        DefaultFormat = format
    };

    private static ReportDataSet MakeDataSet(string reportId = "cost.test") => new()
    {
        ReportId = reportId,
        ExecutedAtUtc = DateTime.UtcNow,
        Columns = ["Col1"],
        Rows = [new Dictionary<string, object?> { ["Col1"] = "val" }]
    };

    private static ReportOutput MakeOutput(string format = "json") => new()
    {
        Content = "{}"u8.ToArray(),
        ContentType = "application/json",
        FileName = "test.json",
        Format = format
    };

    private static ReportPipelineService BuildPipeline(
        ReportDefinition? report = null,
        IDataFetcher? fetcher = null,
        ITransformer? transformer = null,
        IRenderer? renderer = null)
    {
        var catalogMock = new Mock<IReportCatalogProvider>();
        var theReport = report ?? MakeReport();
        catalogMock.Setup(c => c.GetByIdAsync(theReport.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(theReport);
        catalogMock.Setup(c => c.GetByIdAsync(
            It.Is<string>(s => s != theReport.Id), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((ReportDefinition?)null);

        catalogMock.SetupGet(c => c.IsServingLastKnownGood).Returns(false);

        var validatorMock = new Mock<IParameterValidator>();
        validatorMock.Setup(v => v.Validate(It.IsAny<ReportDefinition>(),
                                            It.IsAny<IReadOnlyDictionary<string, string?>>()))
                     .Returns(Array.Empty<string>());

        var fetcherMock = fetcher ?? CreateFetcherMock(theReport.Id);
        var transformerMock = transformer ?? CreatePassThroughTransformer();
        var rendererMock = renderer ?? CreateRendererMock("json");

        return new ReportPipelineService(
            catalogMock.Object,
            validatorMock.Object,
            [fetcherMock],
            [transformerMock],
            [rendererMock],
            NullLogger<ReportPipelineService>.Instance);
    }

    private static IDataFetcher CreateFetcherMock(string reportId)
    {
        var mock = new Mock<IDataFetcher>();
        mock.SetupGet(f => f.ReportId).Returns(reportId);
        mock.SetupGet(f => f.DataSource).Returns("Db2Direct");
        mock.Setup(f => f.FetchAsync(It.IsAny<ReportDefinition>(),
                                     It.IsAny<IReadOnlyDictionary<string, string?>>(),
                                     It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeDataSet(reportId));
        return mock.Object;
    }

    private static ITransformer CreatePassThroughTransformer()
    {
        var mock = new Mock<ITransformer>();
        mock.SetupGet(t => t.ReportId).Returns("*");
        mock.Setup(t => t.TransformAsync(It.IsAny<ReportDataSet>(), It.IsAny<ReportDefinition>(),
                                         It.IsAny<IReadOnlyDictionary<string, string?>>(),
                                         It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReportDataSet ds, ReportDefinition _, IReadOnlyDictionary<string, string?> _, CancellationToken _) => ds);
        return mock.Object;
    }

    private static IRenderer CreateRendererMock(string format)
    {
        var mock = new Mock<IRenderer>();
        mock.SetupGet(r => r.Format).Returns(format);
        mock.Setup(r => r.RenderAsync(It.IsAny<ReportDataSet>(), It.IsAny<ReportDefinition>(),
                                      It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeOutput(format));
        return mock.Object;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ValidRequest_ReturnsOutput()
    {
        var pipeline = BuildPipeline();

        var result = await pipeline.ExecuteAsync("cost.test", "json",
            new Dictionary<string, string?>(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Format.Should().Be("json");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownReportId_ReturnsNull()
    {
        var pipeline = BuildPipeline();

        var result = await pipeline.ExecuteAsync("does.not.exist", "json",
            new Dictionary<string, string?>(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedFormat_ThrowsReportFormatException()
    {
        var report = MakeReport();
        var pipeline = BuildPipeline(report: report);

        var act = async () => await pipeline.ExecuteAsync(report.Id, "pdf",
            new Dictionary<string, string?>(), CancellationToken.None);

        await act.Should().ThrowAsync<ReportFormatException>();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidParameters_ThrowsReportValidationException()
    {
        var report = MakeReport();

        var catalogMock = new Mock<IReportCatalogProvider>();
        catalogMock.Setup(c => c.GetByIdAsync(report.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(report);
        catalogMock.SetupGet(c => c.IsServingLastKnownGood).Returns(false);

        var validatorMock = new Mock<IParameterValidator>();
        validatorMock.Setup(v => v.Validate(It.IsAny<ReportDefinition>(),
                                            It.IsAny<IReadOnlyDictionary<string, string?>>()))
                     .Returns(["Parameter 'asAtDate' is required."]);

        var pipeline = new ReportPipelineService(
            catalogMock.Object,
            validatorMock.Object,
            [CreateFetcherMock(report.Id)],
            [CreatePassThroughTransformer()],
            [CreateRendererMock("json")],
            NullLogger<ReportPipelineService>.Instance);

        var act = async () => await pipeline.ExecuteAsync(report.Id, "json",
            new Dictionary<string, string?>(), CancellationToken.None);

        await act.Should().ThrowAsync<ReportValidationException>()
                 .WithMessage("*asAtDate*");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyFormat_UsesDefaultFormat()
    {
        var report = MakeReport(format: "json");
        var pipeline = BuildPipeline(report: report);

        var result = await pipeline.ExecuteAsync(report.Id, string.Empty,
            new Dictionary<string, string?>(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Format.Should().Be("json");
    }

    [Fact]
    public async Task PreviewAsync_AlwaysReturnsJson()
    {
        var report = MakeReport();
        var pipeline = BuildPipeline(report: report);

        var result = await pipeline.PreviewAsync(report.Id,
            new Dictionary<string, string?>(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Format.Should().Be("json");
    }
}
