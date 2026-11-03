using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Reporting.Api.Controllers;
using Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class BuyerViewSyncServiceTests
{
    private static readonly SyncRunResult Applied =
        new("D", "MSP2027050320241103D100.xlsx", SyncRunResult.Applied, null, 6396, 131, 6265, 5, 0);

    private static readonly SyncRunResult FailedRun =
        new("D", "MSP2027050320241103D100.xlsx", SyncRunResult.Failed, "Refused: 100 rows against 6396");

    // CheckIntervalMinutes is large so the timer never fires during a test.
    private static BuyerViewSyncService Service(bool enabled, Func<IReadOnlyList<SyncRunResult>> run) =>
        new(new BuyerViewSyncOptions { Enabled = enabled, CheckIntervalMinutes = 600 }, run, TimeProvider.System,
            NullLogger<BuyerViewSyncService>.Instance);

    private static BuyerViewController Controller(BuyerViewSyncService service) =>
        new(service, NullLogger<BuyerViewController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { TraceIdentifier = "test-trace" } },
        };

    // ── Window ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("05:44", false)]
    [InlineData("05:45", true)]
    [InlineData("07:00", true)]
    [InlineData("09:30", true)]
    [InlineData("09:31", false)]
    [InlineData("23:00", false)]
    public void IsInWindow_DefaultWindow(string time, bool expected) =>
        BuyerViewSyncService.IsInWindow(TimeOnly.Parse(time), "05:45", "09:30").Should().Be(expected);

    // ── Running ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunNow_Disabled_DoesNotSync()
    {
        var ran = false;
        var service = Service(enabled: false, () => { ran = true; return [Applied]; });

        var status = await service.RunNowAsync();

        status.State.Should().Be(BuyerViewSyncState.Disabled);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task RunNow_Enabled_SyncsAndKeepsTheResult()
    {
        var service = Service(enabled: true, () => [Applied]);

        var status = await service.RunNowAsync();

        status.State.Should().Be(BuyerViewSyncState.Completed);
        status.Results.Should().ContainSingle().Which.LinesNew.Should().Be(131);
        service.LastStatus.Should().Be(status);
    }

    [Fact]
    public async Task RunNow_ADivisionFailed_ReportsFailed()
    {
        var status = await Service(enabled: true, () => [FailedRun]).RunNowAsync();

        status.State.Should().Be(BuyerViewSyncState.Failed);
    }

    [Fact]
    public async Task RunNow_StoreOrFolderThrows_ReportsFailedWithoutThrowing()
    {
        var status = await Service(enabled: true, () => throw new IOException("The network path was not found.")).RunNowAsync();

        status.State.Should().Be(BuyerViewSyncState.Failed);
        status.Error.Should().Contain("network path");
    }

    [Fact]
    public async Task RunNow_WhileAnotherRunIsInProgress_DoesNotStartASecond()
    {
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var service = Service(enabled: true, () => { Interlocked.Increment(ref runs); release.Wait(); return [Applied]; });

        var first = service.RunNowAsync();
        await Task.Delay(100);
        var second = await service.RunNowAsync();
        release.Set();
        await first;

        second.State.Should().Be(BuyerViewSyncState.InProgress);
        runs.Should().Be(1);
    }

    // ── Endpoint ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostSync_Applied_Returns200()
    {
        var result = await Controller(Service(enabled: true, () => [Applied])).Sync(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task PostSync_Disabled_Returns503SoTheScheduledTaskNotices()
    {
        var result = await Controller(Service(enabled: false, () => [Applied])).Sync(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task PostSync_Failed_Returns503()
    {
        var result = await Controller(Service(enabled: true, () => [FailedRun])).Sync(CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GetSync_ReturnsTheLastResultWithoutRunning()
    {
        var runs = 0;
        var service = Service(enabled: true, () => { runs++; return [Applied]; });
        await service.RunNowAsync();

        var result = Controller(service).Status();

        result.Should().BeOfType<OkObjectResult>();
        runs.Should().Be(1);
    }
}
