using FluentAssertions;
using Reporting.Infrastructure.ExchangeRate;

namespace Reporting.Tests.ExchangeRate;

/// <summary>
/// Unit tests for Db2ExchangeRateWriter — specifically the M3 date/time
/// conversion utilities (no DB2 connection required).
/// </summary>
public sealed class Db2ExchangeRateWriterTests
{
    // ── ToM3Date ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2023, 1, 3,  20230103)]
    [InlineData(2026, 3, 19, 20260319)]
    [InlineData(2026, 12, 31, 20261231)]
    [InlineData(2000, 1, 1, 20000101)]
    public void ToM3Date_ValidDate_ReturnsCorrectInteger(
        int year, int month, int day, int expected)
    {
        var date = new DateOnly(year, month, day);

        var result = Db2ExchangeRateWriter.ToM3Date(date);

        result.Should().Be(expected);
    }

    // ── ToM3Time ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(10, 0, 0, 100000)]
    [InlineData(23, 59, 59, 235959)]
    [InlineData(9, 30, 0, 93000)]
    public void ToM3Time_ValidTime_ReturnsCorrectInteger(
        int hour, int minute, int second, int expected)
    {
        var dt = new DateTime(2026, 3, 19, hour, minute, second);

        var result = Db2ExchangeRateWriter.ToM3Time(dt);

        result.Should().Be(expected);
    }

    // ── Date round-trip validation ────────────────────────────────────────────────

    [Fact]
    public void ToM3Date_IsEightDigits_ForAnyDateInRange()
    {
        // M3 date must always be 8 digits (YYYYMMDD)
        var dates = new[]
        {
            new DateOnly(2000, 1, 1),
            new DateOnly(2023, 1, 3),
            new DateOnly(2026, 12, 31)
        };

        foreach (var date in dates)
        {
            var result = Db2ExchangeRateWriter.ToM3Date(date);
            result.ToString().Should().HaveLength(8,
                $"Date {date} should produce 8-digit M3 date");
        }
    }
}
