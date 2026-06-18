using FluentAssertions;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Tests.Pipeline;

public sealed class MovexDateConverterTests
{
    [Theory]
    [InlineData(20260101, 2026, 1, 1)]
    [InlineData(20261231, 2026, 12, 31)]
    [InlineData(20000229, 2000, 2, 29)]   // leap year
    public void FromMovexInt_ValidDate_ReturnsCorrectDateTime(int movexDate, int year, int month, int day)
    {
        var result = MovexDateConverter.FromMovexInt(movexDate);

        result.Should().Be(new DateTime(year, month, day));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99999)]
    public void FromMovexInt_ZeroOrNegative_ReturnsNull(int movexDate)
    {
        var result = MovexDateConverter.FromMovexInt(movexDate);

        result.Should().BeNull();
    }

    [Fact]
    public void ToMovexInt_Date_ReturnsYYYYMMDD()
    {
        var date = new DateTime(2026, 4, 15);

        var result = MovexDateConverter.ToMovexInt(date);

        result.Should().Be(20260415);
    }

    [Fact]
    public void ParseParamToMovexInt_ValidDateString_ReturnsMovexInt()
    {
        var result = MovexDateConverter.ParseParamToMovexInt("2026-04-15");

        result.Should().Be(20260415);
    }

    [Fact]
    public void ParseParamToMovexInt_WithLeadingWhitespace_Trims()
    {
        var result = MovexDateConverter.ParseParamToMovexInt("  2026-01-01  ");

        result.Should().Be(20260101);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026/04/15")]
    [InlineData("")]
    public void ParseParamToMovexInt_InvalidFormat_Throws(string input)
    {
        var act = () => MovexDateConverter.ParseParamToMovexInt(input);

        act.Should().Throw<Exception>();
    }

    [Fact]
    public void RoundTrip_DateToMovexIntAndBack_IsIdentical()
    {
        var original = new DateTime(2026, 6, 30);

        var movexInt = MovexDateConverter.ToMovexInt(original);
        var roundTripped = MovexDateConverter.FromMovexInt(movexInt);

        roundTripped.Should().Be(original);
    }
}
