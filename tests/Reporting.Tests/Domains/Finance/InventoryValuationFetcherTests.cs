using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.Domains.Finance;

namespace Reporting.Tests.Domains.Finance;

/// <summary>
/// Query construction for the Inventory Valuation report. Running it needs DB2; these cover the
/// token substitution, positional parameter order, the book rates read from CCURRA, and the
/// [CHANGED] fixes to Karen's SQL.
/// </summary>
public sealed class InventoryValuationFetcherTests
{
    private static readonly IReadOnlyDictionary<string, string> Schemas =
        new Dictionary<string, string> { ["100"] = "MVXCDTA", ["300"] = "MVXC300" };

    private static readonly InventoryValuationOptions Options = new()
    {
        RateDivision = "D", SpotRateType = "99", BookRateType = "1"
    };

    // Warehouses in scope come from the catalog's allowed values, not from code.
    private static readonly ReportDefinition Report = new()
    {
        Id = InventoryValuationFetcher.Id,
        DisplayName = "Inventory Valuation",
        Domain = "Finance",
        DataSource = "Db2Direct",
        MaxRows = 100_000,
        Parameters =
        [
            new ReportParameter
            {
                Name = "warehouses", DisplayName = "Warehouses", Type = "multiselect", Required = false,
                AllowedValues = ["D01", "D27", "050", "MRB"]
            }
        ]
    };

    // As read from CCURRA rate type 1, division D on 2026-10-14.
    private static readonly IReadOnlyList<BookRate> BookRates =
    [
        new("EUR", 0.61m, new DateOnly(2026, 7, 1)),
        new("USD", 0.70m, new DateOnly(2019, 7, 1)),
    ];

    private static InventoryValuationFetcher CreateFetcher(string defaultCompany = "100", InventoryValuationOptions? options = null) =>
        new("DSN=test", Schemas, defaultCompany, options ?? Options, 300, Mock.Of<ILogger<InventoryValuationFetcher>>());

    private static InventoryValuationQuery Build(params (string Key, string? Value)[] values) =>
        CreateFetcher().BuildQuery(Report, values.ToDictionary(v => v.Key, v => v.Value), BookRates);

    private static int Placeholders(string sql) => sql.Count(c => c == '?');

    [Fact]
    public void ReportId_MatchesCatalogId()
    {
        CreateFetcher().ReportId.Should().Be("finance.inventory-valuation");
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_NoParameters_AllCatalogWarehouses_DefaultCompany_LatestRate()
    {
        var query = Build();

        query.Company.Should().Be("100");
        query.Warehouses.Should().Equal("D01", "D27", "050", "MRB");
        query.RevalueAtDate.Should().BeNull();
        query.Parameters.Should().Equal("D01", "D27", "050", "MRB", "D01", "D27", "050", "MRB");
        Placeholders(query.Sql).Should().Be(query.Parameters.Count);
    }

    [Fact]
    public void BuildQuery_LeavesNoTokensOrComments()
    {
        Build().Sql.Should().NotContain("{{").And.NotContain("--");
    }

    // ── Company → schema, CONO, and CCURRA settings from config ───────────────────

    [Fact]
    public void BuildQuery_Company300_UsesItsSchemaAndConoEverywhere()
    {
        var query = Build(("companyCode", "300"));

        query.Sql.Should().Contain("MVXC300.FCAAVP").And.Contain("MVXC300.CCURRA")
            .And.Contain("IB.MBCONO = 300").And.Contain("CUCONO = 300")
            .And.NotContain("MVXCDTA").And.NotContain("= 100");
    }

    [Fact]
    public void BuildQuery_SpotRateTypeAndDivisionComeFromConfig()
    {
        var options = new InventoryValuationOptions { RateDivision = "X", SpotRateType = "98", BookRateType = "1" };
        var sql = CreateFetcher(options: options)
            .BuildQuery(Report, new Dictionary<string, string?>(), BookRates).Sql;

        Regex.Matches(sql, "CUDIVI = 'X'").Count.Should().Be(8);
        Regex.Matches(sql, "CUCRTP = '98'").Count.Should().Be(8);
        sql.Should().NotContain("CUCRTP = '99'");
    }

    [Theory]
    [InlineData("D'; DROP", "99", "1")]
    [InlineData("D", "", "1")]
    [InlineData("D", "99", "1 OR 1=1")]
    public void Options_ThatAreNotShortCodes_AreRejected(string division, string spot, string book)
    {
        var act = () => CreateFetcher(options: new InventoryValuationOptions
        {
            RateDivision = division, SpotRateType = spot, BookRateType = book
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*InventoryValuation*");
    }

    // ── Filters and parameter order ───────────────────────────────────────────────

    [Fact]
    public void BuildQuery_AllFilters_ParametersFollowPlaceholderOrder()
    {
        // Text order: Part A warehouses, Part A filters, Part A revaluation date,
        // Part B revaluation date, Part B warehouses, Part B filters.
        var query = Build(
            ("warehouses", "D01,050"),
            ("itemNumber", " DC100018 "),
            ("itemGroup", "1185"),
            ("revalueAtDate", "2026-09-30"));

        query.Parameters.Should().Equal(
            "D01", "050", "DC100018", "1185", 20260930,
            20260930, "D01", "050", "DC100018", "1185");
        Placeholders(query.Sql).Should().Be(query.Parameters.Count);
        query.Sql.Should().Contain("IB.MBITNO = ?").And.Contain("ITM.MMITGR = ?")
            .And.Contain("CR2.CUCUTD <= ?").And.Contain("CU2.CUCUTD <= ?");
    }

    [Fact]
    public void BuildQuery_RevalueDate_IsCarriedToTheRun()
    {
        Build(("revalueAtDate", "2026-09-30")).ToRun().RevalueAtDate.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public void BuildQuery_WarehouseNotInTheCatalog_Throws()
    {
        var act = () => Build(("warehouses", "D01,X99"));

        act.Should().Throw<ArgumentException>().WithMessage("*X99*");
    }

    [Fact]
    public void BuildQuery_UnknownCompany_Throws()
    {
        var act = () => Build(("companyCode", "400"));

        act.Should().Throw<ArgumentException>().WithMessage("*400*");
    }

    // ── Book rates — read from CCURRA, every CASE generated from them ─────────────

    [Fact]
    public void BuildQuery_BookRateCaseIsGeneratedFromTheRatesForAllSixValuationColumns()
    {
        var sql = Build().Sql;

        Regex.Matches(sql, @"WHEN 'USD' THEN 0\.700000").Count.Should().Be(6);
        Regex.Matches(sql, @"WHEN 'EUR' THEN 0\.610000").Count.Should().Be(6);
        sql.Should().NotContain("WHEN 'GBP'");     // only the rates supplied
    }

    [Fact]
    public void BuildQuery_NoBookRates_Throws()
    {
        // Falling back to 1.0 would leave every foreign layer un-converted and the total wrong.
        var act = () => CreateFetcher().BuildQuery(Report, new Dictionary<string, string?>(), []);

        act.Should().Throw<InvalidOperationException>().WithMessage("*book rate*");
    }

    [Fact]
    public void BuildBookRateQuery_ReadsLatestRatePerCurrency_ForConfiguredTypeAndDivision()
    {
        var (sql, parameters) = CreateFetcher().BuildBookRateQuery("MVXCDTA", "100");

        sql.Should().Contain("MVXCDTA.CCURRA").And.Contain("MAX(").And.NotContain("@");
        parameters.Should().Equal(100, "D", "1");
        Placeholders(sql).Should().Be(parameters.Count);
    }

    // ── [CHANGED] fixes ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_BothHalvesOfTheUnionReturnTheSameColumnsInTheSameOrder()
    {
        // UNION ALL pairs columns by POSITION. Karen's Part B had STANDARD_COST / MOVEX_STOCK_VALUE
        // after ENTRY_*, so they landed under ENTRY_TIME / TIME_SUFFIX and came out blank.
        var halves = Build().Sql.Split("UNION ALL");

        halves.Should().HaveCount(2);
        var partA = TopLevelColumns(halves[0]);
        var partB = TopLevelColumns(halves[1]);

        partA.Should().HaveCount(26);   // Karen's 25 columns + VALUE_SOURCE
        partB.Should().Equal(partA);
        partA.Should().ContainInOrder("STANDARD_COST", "MOVEX_STOCK_VALUE", "ENTRY_DATE", "ENTRY_TIME", "TIME_SUFFIX");
        partA.Last().Should().Be("VALUE_SOURCE");
    }

    [Fact]
    public void TopLevelColumns_IgnoresCommasAndFromInsideBracketsAndSubqueries()
    {
        var columns = TopLevelColumns("SELECT A.X, COALESCE(A.Y, 0) AS Y2, (SELECT 1 FROM T) AS Z FROM T2");

        columns.Should().Equal("X", "Y2", "Z");
    }

    /// <summary>Column names (alias, or the last part of a plain column) of a statement's outermost SELECT.</summary>
    private static List<string> TopLevelColumns(string sql)
    {
        var depth = 0;
        var from = -1;
        for (var i = sql.IndexOf("SELECT", StringComparison.Ordinal) + 6; i < sql.Length && from < 0; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')') depth--;
            else if (depth == 0 && string.Compare(sql, i, "FROM", 0, 4, StringComparison.Ordinal) == 0
                     && !char.IsLetterOrDigit(sql[i - 1]) && !char.IsLetterOrDigit(sql[i + 4]))
                from = i;
        }

        var list = sql[(sql.IndexOf("SELECT", StringComparison.Ordinal) + 6)..from];
        var columns = new List<string>();
        var current = new System.Text.StringBuilder();
        depth = 0;
        foreach (var c in list + ",")
        {
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ',' && depth == 0)
            {
                var text = current.ToString().Trim();
                var alias = System.Text.RegularExpressions.Regex.Match(text, @"(?:\bAS\s+|\.)([A-Za-z0-9_]+)\s*$");
                columns.Add(alias.Success ? alias.Groups[1].Value : text);
                current.Clear();
            }
            else current.Append(c);
        }
        return columns;
    }

    [Fact]
    public void BuildQuery_FetchesOneRowBeyondMaxRows()
    {
        Build().Sql.Should().Contain("FETCH FIRST 100001 ROWS ONLY");
    }
}
