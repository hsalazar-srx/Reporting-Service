using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Domains.SupplyChain;

namespace Reporting.Tests.Domains.SupplyChain;

/// <summary>
/// Unit tests for PoIssuedByBuyerFetcher query construction: SQL shape, positional parameter order,
/// the default period and parameter validation. Executing the query needs DB2.
/// </summary>
public sealed class PoIssuedByBuyerFetcherTests
{
    private static readonly IReadOnlyDictionary<string, string> Schemas =
        new Dictionary<string, string> { ["100"] = "MVXCDTA", ["300"] = "MVXC300" };

    // Friday 23 Oct 2026; its week starts Monday 19 Oct.
    private static readonly DateTime Today = new(2026, 10, 23);

    private static readonly ReportDefinition Report = new()
    {
        Id = PoIssuedByBuyerFetcher.Id,
        DisplayName = "POs Issued by Buyer",
        Domain = "SupplyChain",
        DataSource = "Db2Direct",
        MaxRows = 100_000,
        Parameters =
        [
            new ReportParameter { Name = "divisions", DisplayName = "Divisions", Type = "multiselect",
                Required = false, DefaultValue = "D,L", AllowedValues = ["D", "L"] },
            new ReportParameter { Name = "orderTypes", DisplayName = "Order types", Type = "multiselect",
                Required = false, DefaultValue = "010", AllowedValues = ["010", "040", "PTP", "070"] }
        ]
    };

    private static PoIssuedByBuyerFetcher CreateFetcher(string defaultCompany = "100") =>
        new("DSN=test", Schemas, defaultCompany, 120, Mock.Of<ILogger<PoIssuedByBuyerFetcher>>(), () => Today);

    private static IReadOnlyDictionary<string, string?> Params(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void ReportId_MatchesCatalogId()
    {
        CreateFetcher().ReportId.Should().Be("supplychain.po-issued-by-buyer");
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_NoParameters_CountsTwelveFullWeeksPlusTheCurrentOne()
    {
        var run = CreateFetcher().BuildQuery(Report, Params()).Run;

        run.To.Should().Be(Today);
        run.From.Should().Be(new DateTime(2026, 7, 27));   // Monday, 12 weeks before Monday 19 Oct
        run.From.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void BuildQuery_NoParameters_UsesBothDivisionsAndOrderType010()
    {
        var query = CreateFetcher().BuildQuery(Report, Params());

        query.Run.Divisions.Should().Equal("D", "L");
        query.Run.OrderTypes.Should().Equal("010");
        query.Parameters.Should().Equal("100", "D", "L", "010", 20260727, 20261023, 20260727, 20261023);
    }

    // ── SQL shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_ReadsMpheadInTheCompanysSchema()
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("companyCode", "300")));

        query.Sql.Should().Contain("MVXC300.MPHEAD");
        query.Parameters[0].Should().Be("300");
    }

    [Fact]
    public void BuildQuery_LeavesOutDeletedPos()
    {
        // Lowest status 00 with highest 99 and no lines = deleted (DBSRV check, 23 Oct 2026).
        CreateFetcher().BuildQuery(Report, Params()).Sql.Should().Contain("h.IAPUSL <> '00'");
    }

    [Fact]
    public void BuildQuery_SelectsPosIssuedOrCreatedInThePeriod()
    {
        var sql = CreateFetcher().BuildQuery(Report, Params()).Sql;

        sql.Should().Contain("(h.IALEDT BETWEEN ? AND ?) OR (h.IARGDT BETWEEN ? AND ?)");
    }

    [Fact]
    public void BuildQuery_PlaceholderCountMatchesParameterCount()
    {
        var query = CreateFetcher().BuildQuery(Report,
            Params(("divisions", "D"), ("orderTypes", "010,040,PTP")));

        query.Sql.Count(c => c == '?').Should().Be(query.Parameters.Count);
        query.Sql.Should().NotContain("@");
    }

    [Fact]
    public void BuildQuery_FetchesOneRowBeyondMaxRows_SoTruncationIsDetectable()
    {
        CreateFetcher().BuildQuery(Report, Params()).Sql.Should().Contain("FETCH FIRST 100001 ROWS ONLY");
    }

    // ── Parameters ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_EnteredPeriod_IsPassedAsMovexDates()
    {
        var query = CreateFetcher().BuildQuery(Report,
            Params(("fromDate", "2026-08-03"), ("toDate", "2026-10-19")));

        query.Parameters.TakeLast(4).Should().Equal(20260803, 20261019, 20260803, 20261019);
    }

    [Fact]
    public void BuildQuery_DivisionsAndOrderTypes_AreMatchedToTheCatalogCase()
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("divisions", "l"), ("orderTypes", "ptp, 010")));

        query.Run.Divisions.Should().Equal("L");
        query.Run.OrderTypes.Should().Equal("PTP", "010");
    }

    [Fact]
    public void BuildQuery_FromAfterTo_IsAValidationError()
    {
        var act = () => CreateFetcher().BuildQuery(Report,
            Params(("fromDate", "2026-10-20"), ("toDate", "2026-10-01")));

        act.Should().Throw<ReportValidationException>()
            .Which.Errors.Should().ContainSingle().Which.Should().Contain("after");
    }

    [Theory]
    [InlineData("fromDate", "03/08/2026")]
    [InlineData("toDate", "yesterday")]
    public void BuildQuery_DateNotInIsoFormat_IsAValidationError(string key, string value)
    {
        var act = () => CreateFetcher().BuildQuery(Report, Params((key, value)));

        act.Should().Throw<ReportValidationException>().Which.Errors.Should().ContainSingle()
            .Which.Should().Contain(key);
    }

    [Fact]
    public void BuildQuery_UnknownOrderType_IsAValidationError()
    {
        var act = () => CreateFetcher().BuildQuery(Report, Params(("orderTypes", "010,999")));

        act.Should().Throw<ReportValidationException>().Which.Errors.Should().ContainSingle()
            .Which.Should().Contain("999");
    }

    [Fact]
    public void BuildQuery_UnknownCompany_Throws()
    {
        var act = () => CreateFetcher().BuildQuery(Report, Params(("companyCode", "400")));

        act.Should().Throw<ArgumentException>().WithMessage("*400*");
    }

    // ── Weeks and row limit ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-19", "2026-10-19")]   // Monday
    [InlineData("2026-10-23", "2026-10-19")]   // Friday
    [InlineData("2026-10-25", "2026-10-19")]   // Sunday belongs to the week before
    public void WeekStart_IsTheMondayOfThatWeek(string date, string monday)
    {
        PoIssuedByBuyerFetcher.WeekStart(DateTime.Parse(date)).Should().Be(DateTime.Parse(monday));
    }

    [Fact]
    public void EnsureWithinRowLimit_MoreRowsThanMaxRows_AsksForAShorterPeriod()
    {
        var act = () => PoIssuedByBuyerFetcher.EnsureWithinRowLimit(100_001, 100_000);

        act.Should().Throw<ReportValidationException>().Which.Errors.Should().ContainSingle()
            .Which.Should().Contain("shorter period");
    }
}
