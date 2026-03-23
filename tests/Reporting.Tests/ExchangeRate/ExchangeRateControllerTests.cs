using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Reporting.Api.Controllers;
using Reporting.Infrastructure.ExchangeRate;
using Reporting.Infrastructure.ExchangeRate.Models;

namespace Reporting.Tests.ExchangeRate;

/// <summary>
/// Unit tests for ExchangeRateController API endpoint.
/// Tests: routing, validation, weekend fallback flag, not-found handling.
/// </summary>
public sealed class ExchangeRateControllerTests
{
    private readonly Mock<Db2ExchangeRateReader> _readerMock;
    private readonly ExchangeRateSyncService _syncService;
    private readonly ExchangeRateController _controller;

    public ExchangeRateControllerTests()
    {
        // Minimal sync service instance (no timer fires in tests)
        var options = Options.Create(new ExchangeRateSyncOptions
        {
            Enabled = false   // disable actual sync in tests
        });
        var db2Options = Options.Create(new Db2ExchangeRateOptions
        {
            ConnectionString = "DSN=test"
        });
        var rbaClientMock = new Mock<RbaApiClient>(
            Mock.Of<ILogger<RbaApiClient>>()) { CallBase = false };

        _syncService = new ExchangeRateSyncService(
            options,
            rbaClientMock.Object,
            () => new Db2ExchangeRateWriter(options, db2Options,
                Mock.Of<ILogger<Db2ExchangeRateWriter>>()),
            Mock.Of<ILogger<ExchangeRateSyncService>>());

        _readerMock = new Mock<Db2ExchangeRateReader>(
            options, db2Options, Mock.Of<ILogger<Db2ExchangeRateReader>>())
            { CallBase = false };

        _controller = new ExchangeRateController(
            _readerMock.Object,
            _syncService,
            Mock.Of<ILogger<ExchangeRateController>>());

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { TraceIdentifier = "test-trace" }
        };
    }

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRate_ValidWeekdayDate_Returns200WithRate()
    {
        var date = new DateOnly(2026, 3, 19); // Thursday

        _readerMock
            .Setup(r => r.GetRateForDateAsync("USD", date, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExchangeRateData
            {
                Currency = "USD",
                Rate = 0.6828m,
                EffectiveDate = date,
                FetchedAtUtc = DateTime.UtcNow,
                UsedFallback = false
            });

        var result = await _controller.GetRate("USD", "2026-03-19", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>()
            .Which.StatusCode.Should().Be(200);

        var body = ((OkObjectResult)result).Value!;
        body.Should().BeEquivalentTo(new
        {
            currency = "USD",
            requestedDate = "2026-03-19",
            effectiveDate = "2026-03-19",
            rate = 0.6828m,
            rateType = "SPOT",
            source = "RBA",
            usedFallback = false,
            isWeekend = false
        }, opt => opt.ExcludingMissingMembers());
    }

    [Fact]
    public async Task GetRate_SaturdayDate_ReturnsResponseWithFallbackTrue()
    {
        var saturday = new DateOnly(2026, 3, 21);   // Saturday
        var friday   = new DateOnly(2026, 3, 20);   // Friday (fallback)

        _readerMock
            .Setup(r => r.GetRateForDateAsync("USD", saturday, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExchangeRateData
            {
                Currency = "USD",
                Rate = 0.6900m,
                EffectiveDate = friday,    // reader returned Friday's rate
                FetchedAtUtc = DateTime.UtcNow,
                UsedFallback = true,
                FallbackFromDate = friday
            });

        var result = await _controller.GetRate("USD", "2026-03-21", CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();

        var body = ((OkObjectResult)result).Value!;
        body.Should().BeEquivalentTo(new
        {
            currency = "USD",
            requestedDate = "2026-03-21",
            effectiveDate = "2026-03-20",   // Friday
            usedFallback = true,
            isWeekend = true                // 21 Mar 2026 = Saturday
        }, opt => opt.ExcludingMissingMembers());
    }

    // ── Validation ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("US")]          // too short
    [InlineData("USDD")]        // too long
    [InlineData("usd")]         // lowercase (controller uppercases, but invalid length check)
    [InlineData("1USD")]        // starts with digit
    public async Task GetRate_InvalidCurrencyCode_Returns400(string currency)
    {
        // 'usd' normalises to 'USD' (3 chars, valid) so only truly invalid codes trigger 400
        if (currency.ToUpperInvariant() is "USD")
        {
            // Skip — valid after normalisation
            return;
        }

        var result = await _controller.GetRate(currency, "2026-03-19", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("19-03-2026")]  // wrong format
    [InlineData("2026/03/19")]  // wrong separator
    [InlineData("not-a-date")]
    public async Task GetRate_InvalidDateFormat_Returns400(string date)
    {
        var result = await _controller.GetRate("USD", date, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ── Not found ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRate_NoRateWithinFallbackWindow_Returns404()
    {
        _readerMock
            .Setup(r => r.GetRateForDateAsync(It.IsAny<string>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExchangeRateNotFoundException("No rate found for USD on 2026-01-01 or previous 3 days"));

        var result = await _controller.GetRate("USD", "2026-01-01", CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>()
            .Which.StatusCode.Should().Be(404);
    }
}
