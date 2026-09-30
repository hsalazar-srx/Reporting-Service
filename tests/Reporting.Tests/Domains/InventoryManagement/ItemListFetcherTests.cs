using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.Domains.InventoryManagement;

namespace Reporting.Tests.Domains.InventoryManagement;

/// <summary>
/// Unit tests for ItemListFetcher query construction.
///
/// Executing the query requires DB2 connectivity; these tests cover the SQL text, the
/// positional parameter order, and company → schema resolution, which is where the
/// behaviour that matters (and the traps) live.
/// </summary>
public sealed class ItemListFetcherTests
{
    private static readonly IReadOnlyDictionary<string, string> Schemas =
        new Dictionary<string, string> { ["100"] = "MVXCDTA", ["300"] = "MVXC300" };

    private static readonly ReportDefinition Report = new()
    {
        Id = ItemListFetcher.Id,
        DisplayName = "Inventory List",
        Domain = "InventoryManagement",
        DataSource = "Db2Direct",
        MaxRows = 100_000
    };

    private static ItemListFetcher CreateFetcher(string defaultCompany = "300") =>
        new("DSN=test", Schemas, defaultCompany, 120, Mock.Of<ILogger<ItemListFetcher>>());

    private static IReadOnlyDictionary<string, string?> Params(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    // ── Identity ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReportId_MatchesCatalogId()
    {
        CreateFetcher().ReportId.Should().Be("inventory.item-list");
    }

    [Fact]
    public void Columns_AreTheVerbatimLegacyHeaders()
    {
        ItemListFetcher.Columns.Should().Equal(
            "Item number", "Name", "Status", "Item group", "Item group Description");
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_NoParameters_UsesConfiguredCompanyAndExcludesStatus90()
    {
        var query = CreateFetcher(defaultCompany: "300").BuildQuery(Report, Params());

        query.Company.Should().Be("300");
        query.Parameters.Should().Equal("300", "90");
    }

    [Fact]
    public void BuildQuery_ProductionDefaultCompany_UsesProductionSchema()
    {
        var query = CreateFetcher(defaultCompany: "100").BuildQuery(Report, Params());

        query.Sql.Should().Contain("MVXCDTA.MITMAS");
        query.Parameters[0].Should().Be("100");
    }

    // ── Company → schema (CONO 100 = MVXCDTA, CONO 300 = MVXC300) ────────────────

    [Theory]
    [InlineData("100", "MVXCDTA")]
    [InlineData("300", "MVXC300")]
    public void BuildQuery_CompanyParameter_SelectsThatCompanysSchemaForBothTables(string company, string schema)
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("companyCode", company)));

        query.Sql.Should().Contain($"{schema}.MITMAS").And.Contain($"{schema}.CSYTAB");
        query.Parameters[0].Should().Be(company);
    }

    [Fact]
    public void BuildQuery_UnknownCompany_Throws()
    {
        var act = () => CreateFetcher().BuildQuery(Report, Params(("companyCode", "400")));

        act.Should().Throw<ArgumentException>().WithMessage("*400*");
    }

    // ── Filters and parameter order ───────────────────────────────────────────────

    [Fact]
    public void BuildQuery_ExcludeStatusOverride_IsPassedAsParameter()
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("excludeStatus", "20")));

        query.Parameters.Should().Equal("300", "20");
    }

    [Fact]
    public void BuildQuery_ItemGroup_AddsPredicateAndParameterLast()
    {
        var query = CreateFetcher().BuildQuery(Report,
            Params(("companyCode", "100"), ("itemGroup", " ABC "), ("excludeStatus", "90")));

        query.Sql.Should().Contain("m.MMITGR = ?");
        query.Parameters.Should().Equal("100", "90", "ABC");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildQuery_BlankItemGroup_AddsNoPredicate(string? itemGroup)
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("itemGroup", itemGroup)));

        query.Sql.Should().NotContain("MMITGR = ?");
        query.Parameters.Should().HaveCount(2);
    }

    [Fact]
    public void BuildQuery_PlaceholderCountMatchesParameterCount()
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("itemGroup", "ABC")));

        query.Sql.Count(c => c == '?').Should().Be(query.Parameters.Count);
    }

    // ── SQL shape ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_UsesPositionalParametersOnly()
    {
        var query = CreateFetcher().BuildQuery(Report, Params(("itemGroup", "ABC")));

        query.Sql.Should().NotContain("@");
    }

    [Fact]
    public void BuildQuery_ItemGroupDescription_IsSingleRowSubquery_SoItemsAreNeverDuplicated()
    {
        // CSYTAB is keyed by division and language as well as the code. A plain join could
        // return one row per division/language and duplicate items; the original .dqy
        // inner-joined and silently dropped items with no code row instead.
        var query = CreateFetcher().BuildQuery(Report, Params());

        query.Sql.Should().Contain("CTSTCO = 'ITGR'")
            .And.Contain("FETCH FIRST 1 ROW ONLY")
            .And.NotContain("INNER JOIN");
    }

    [Fact]
    public void BuildQuery_FetchesOneRowBeyondMaxRows_SoTruncationIsDetectable()
    {
        var query = CreateFetcher().BuildQuery(Report, Params());

        query.Sql.Should().Contain("FETCH FIRST 100001 ROWS ONLY");
    }

    [Fact]
    public void BuildQuery_UnlimitedMaxRows_HasNoRowCap()
    {
        var query = CreateFetcher().BuildQuery(Report with { MaxRows = 0 }, Params());

        query.Sql.Should().NotContain("FETCH FIRST 0");
        query.Sql.Should().NotContain("ROWS ONLY");
    }

    // ── Row limit — never return a partial list as if it were complete ────────────

    [Fact]
    public void EnsureWithinRowLimit_MoreRowsThanMaxRows_ThrowsValidationPointingAtTheFilter()
    {
        var act = () => ItemListFetcher.EnsureWithinRowLimit(rowCount: 100_001, maxRows: 100_000);

        act.Should().Throw<Reporting.Core.Pipeline.ReportValidationException>()
            .Which.Errors.Should().ContainSingle()
            .Which.Should().Contain("100000").And.Contain("Item Group");
    }

    [Theory]
    [InlineData(100_000, 100_000)]
    [InlineData(5, 100_000)]
    [InlineData(500_000, 0)]
    public void EnsureWithinRowLimit_AtOrUnderLimit_OrUnlimited_DoesNotThrow(int rowCount, int maxRows)
    {
        var act = () => ItemListFetcher.EnsureWithinRowLimit(rowCount, maxRows);

        act.Should().NotThrow();
    }

    // ── Construction ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_DefaultCompanyWithNoSchema_Throws()
    {
        var act = () => CreateFetcher(defaultCompany: "400");

        act.Should().Throw<InvalidOperationException>().WithMessage("*400*");
    }

    [Fact]
    public void Constructor_SchemaWithUnsafeCharacters_Throws()
    {
        // Schemas are interpolated into SQL text, so they must be plain identifiers.
        var bad = new Dictionary<string, string> { ["300"] = "MVXC300; DROP TABLE X" };

        var act = () => new ItemListFetcher("DSN=test", bad, "300", 120, Mock.Of<ILogger<ItemListFetcher>>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*schema*");
    }
}
