using FluentAssertions;
using Reporting.Infrastructure.ExchangeRate;

namespace Reporting.Tests.ExchangeRate;

/// <summary>
/// Unit tests for RbaApiClient CSV parsing logic.
/// Tests the ParseRate method with representative RBA F11.1 CSV formats.
/// </summary>
public sealed class RbaApiClientTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    // Minimal representative sample of actual RBA F11.1 CSV format.
    // The "Units" row contains bare ISO 4217 currency codes — this is the header row used for lookup.
    private const string SampleCsv = """
        Title,A$1=USD,A$1=GBP,A$1=EUR
        Description,AUD/USD Exchange Rate,AUD/GBP Exchange Rate,AUD/EUR Exchange Rate
        Frequency,Daily,Daily,Daily
        Type,Indicative,Indicative,Indicative
        Units,USD,GBP,EUR
        Source,WM/Reuters,RBA,RBA
        Publication date,19-Mar-2026,19-Mar-2026,19-Mar-2026
        Series ID,FXRUSD,FXRUKPS,FXREUR
        02-Jan-2023,0.6828,0.5623,0.6241
        03-Jan-2023,0.6901,0.5689,0.6310
        """;

    // ── ParseRate ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseRate_ValidCsv_ReturnsMostRecentRow()
    {
        var result = RbaApiClient.ParseRate(SampleCsv, "USD");

        result.Should().NotBeNull();
        result!.Currency.Should().Be("USD");
        result.Rate.Should().Be(0.6901m);   // last row
        result.EffectiveDate.Should().Be(new DateOnly(2023, 1, 3));
        result.UsedFallback.Should().BeFalse();
        result.Source.Should().Be("RBA:F11.1");
    }

    [Fact]
    public void ParseRate_CurrencyNotInCsv_ReturnsNull()
    {
        var result = RbaApiClient.ParseRate(SampleCsv, "JPY");

        result.Should().BeNull();
    }

    [Fact]
    public void ParseRate_CurrencyIsCaseInsensitive()
    {
        var result = RbaApiClient.ParseRate(SampleCsv, "usd");

        result.Should().NotBeNull();
        result!.Currency.Should().Be("USD");
    }

    [Fact]
    public void ParseRate_EmptyCsv_ThrowsInvalidOperationException()
    {
        var act = () => RbaApiClient.ParseRate("", "USD");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public void ParseRate_RealWorldDateFormat_ParsesCorrectly()
    {
        // RBA uses dd-MMM-yyyy format (e.g. "03-Jan-2023")
        var csv = """
            Units,USD
            03-Jan-2023,0.6828
            """;

        var result = RbaApiClient.ParseRate(csv, "USD");

        result.Should().NotBeNull();
        result!.EffectiveDate.Should().Be(new DateOnly(2023, 1, 3));
    }

    [Theory]
    [InlineData(0.0001)]   // minimum valid rate
    [InlineData(10000)]    // maximum valid rate
    [InlineData(0.6828)]   // typical AUD/USD rate
    public void ParseRate_ValidRateRange_ReturnsRate(decimal expectedRate)
    {
        var csv = $"""
            Units,USD
            03-Jan-2023,{expectedRate}
            """;

        var result = RbaApiClient.ParseRate(csv, "USD");

        result!.Rate.Should().Be(expectedRate);
    }

    [Theory]
    [InlineData("0.00009")]   // below minimum
    [InlineData("10001")]     // above maximum
    public void ParseRate_RateOutsideRange_ThrowsInvalidOperationException(string rate)
    {
        var csv = $"""
            Units,USD
            03-Jan-2023,{rate}
            """;

        var act = () => RbaApiClient.ParseRate(csv, "USD");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside valid range*");
    }
}
