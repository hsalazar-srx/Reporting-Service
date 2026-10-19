using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Domains.Finance;

namespace Reporting.Tests.Domains.Finance;

/// <summary>
/// Query construction for the Inventory Valuation report. Running it needs DB2; these cover the
/// token substitution, positional parameter order, the book rates and material overhead entered
/// on the report page (catalog defaults when left alone), and the [CHANGED] fixes to Karen's SQL.
/// </summary>
public sealed class InventoryValuationFetcherTests
{
    private static readonly IReadOnlyDictionary<string, string> Schemas =
        new Dictionary<string, string> { ["100"] = "MVXCDTA", ["300"] = "MVXC300" };

    private static readonly InventoryValuationOptions Options = new()
    {
        RateDivision = "D", SpotRateType = "99"
    };

    // Warehouses in scope, book rates and overhead come from the catalog, not from code.
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
            },
            RateParameter("EUR", "0.61"),
            RateParameter("USD", "0.70"),
            new ReportParameter
            {
                Name = "materialOverheadPercent", DisplayName = "Material overhead %", Type = "decimal",
                Required = false, DefaultValue = "68"
            }
        ]
    };

    private static ReportParameter RateParameter(string currency, string defaultRate) => new()
    {
        Name = $"bookRate{currency}", DisplayName = $"{currency} book rate", Type = "decimal",
        Required = false, DefaultValue = defaultRate, Group = "Book rates"
    };

    private static ReportDefinition ReportWithout(string parameterName) =>
        Report with { Parameters = Report.Parameters.Where(p => p.Name != parameterName).ToList() };

    private static InventoryValuationFetcher CreateFetcher(string defaultCompany = "100", InventoryValuationOptions? options = null) =>
        new("DSN=test", Schemas, defaultCompany, options ?? Options, 300, Mock.Of<ILogger<InventoryValuationFetcher>>());

    private static InventoryValuationQuery Build(params (string Key, string? Value)[] values) =>
        CreateFetcher().BuildQuery(Report, values.ToDictionary(v => v.Key, v => v.Value));

    private static int Placeholders(string sql) => sql.Count(c => c == '?');

    [Fact]
    public void ReportId_MatchesCatalogId()
    {
        CreateFetcher().ReportId.Should().Be("finance.inventory-valuation");
    }

    // ── Defaults ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_NoParameters_AllCatalogWarehouses_DefaultCompany()
    {
        var query = Build();

        query.Company.Should().Be("100");
        query.Warehouses.Should().Equal("D01", "D27", "050", "MRB");
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
        var options = new InventoryValuationOptions { RateDivision = "X", SpotRateType = "98" };
        var sql = CreateFetcher(options: options)
            .BuildQuery(Report, new Dictionary<string, string?>()).Sql;

        Regex.Matches(sql, "CUDIVI = 'X'").Count.Should().Be(8);
        Regex.Matches(sql, "CUCRTP = '98'").Count.Should().Be(8);
        sql.Should().NotContain("CUCRTP = '99'");
    }

    [Theory]
    [InlineData("D'; DROP", "99")]
    [InlineData("D", "")]
    [InlineData("D", "99 OR 1=1")]
    public void Options_ThatAreNotShortCodes_AreRejected(string division, string spot)
    {
        var act = () => CreateFetcher(options: new InventoryValuationOptions
        {
            RateDivision = division, SpotRateType = spot
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*InventoryValuation*");
    }

    // ── Filters and parameter order ───────────────────────────────────────────────

    [Fact]
    public void BuildQuery_AllFilters_ParametersFollowPlaceholderOrder()
    {
        // Text order: Part A warehouses, Part A filters, Part B warehouses, Part B filters.
        var query = Build(
            ("warehouses", "D01,050"),
            ("itemNumber", " DC100018 "),
            ("itemGroup", "1185"));

        query.Parameters.Should().Equal(
            "D01", "050", "DC100018", "1185",
            "D01", "050", "DC100018", "1185");
        Placeholders(query.Sql).Should().Be(query.Parameters.Count);
        query.Sql.Should().Contain("IB.MBITNO = ?").And.Contain("ITM.MMITGR = ?");
    }

    [Fact]
    public void BuildQuery_HasNoRevaluationDate_RevaluationIsAlwaysTheLatestSpotRate()
    {
        // The report page no longer offers a revaluation date; a leftover value is ignored.
        var query = Build(("revalueAtDate", "2026-09-30"));

        query.Sql.Should().NotContain("CUCUTD <= ?").And.NotContain("REVALUE");
        query.Parameters.Should().NotContain(20260930);
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

    // ── Book rates — entered on the report page, every CASE generated from them ────

    [Fact]
    public void BuildQuery_BookRateCaseIsGeneratedFromTheCatalogDefaultsForAllSixValuationColumns()
    {
        var query = Build();

        Regex.Matches(query.Sql, @"WHEN 'USD' THEN 0\.700000").Count.Should().Be(6);
        Regex.Matches(query.Sql, @"WHEN 'EUR' THEN 0\.610000").Count.Should().Be(6);
        query.Sql.Should().NotContain("WHEN 'GBP'");     // only the rates the catalog offers
        query.BookRates.Select(r => (r.Currency, r.Rate)).Should().Equal(("EUR", 0.61m), ("USD", 0.70m));
    }

    [Fact]
    public void BuildQuery_EnteredBookRate_ReplacesTheDefault_AndIsRecordedAgainstIt()
    {
        var query = Build(("bookRateUSD", "0.725"));

        Regex.Matches(query.Sql, @"WHEN 'USD' THEN 0\.725000").Count.Should().Be(6);
        query.Sql.Should().NotContain("THEN 0.700000");
        query.BookRates.Single(r => r.Currency == "USD").Should().Be(new BookRate("USD", 0.725m, 0.70m));
    }

    [Fact]
    public void BuildQuery_BlankBookRate_FallsBackToTheCatalogDefault()
    {
        // A cleared field must not become a zero rate.
        Build(("bookRateUSD", "  ")).BookRates.Single(r => r.Currency == "USD").Rate.Should().Be(0.70m);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.7")]
    [InlineData("abc")]
    [InlineData("0.7); DROP")]
    [InlineData("1000000")]
    public void BuildQuery_BookRateThatIsNotAPlausiblePositiveNumber_IsRejectedByName(string entered)
    {
        var act = () => Build(("bookRateUSD", entered));

        act.Should().Throw<ReportValidationException>().Which.Errors.Should().ContainSingle()
            .Which.Should().Contain("USD book rate");
    }

    [Fact]
    public void BuildQuery_RatesAreReadWithTheInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Build(("bookRateUSD", "0.725")).BookRates.Single(r => r.Currency == "USD").Rate.Should().Be(0.725m);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BuildQuery_NoBookRatesInTheCatalog_Throws()
    {
        // Falling back to 1.0 would leave every foreign layer un-converted and the total wrong.
        var noRates = Report with { Parameters = Report.Parameters.Where(p => !p.Name.StartsWith("bookRate")).ToList() };

        var act = () => CreateFetcher().BuildQuery(noRates, new Dictionary<string, string?>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*book rate*");
    }

    [Fact]
    public void BuildQuery_BookRateParameterWithoutADefault_IsAConfigurationError()
    {
        var noDefault = Report with
        {
            Parameters = Report.Parameters.Select(p => p.Name == "bookRateEUR" ? p with { DefaultValue = null } : p).ToList()
        };

        var act = () => CreateFetcher().BuildQuery(noDefault, new Dictionary<string, string?> { ["bookRateEUR"] = "0.6" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*bookRateEUR*default*");
    }

    [Fact]
    public void BuildQuery_BookRateParameterNamedBadly_IsAConfigurationErrorNotSql()
    {
        var bad = Report with { Parameters = [.. Report.Parameters, RateParameter("US'D", "0.7")] };

        var act = () => CreateFetcher().BuildQuery(bad, new Dictionary<string, string?>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*bookRate*");
    }

    // ── Material overhead ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_MaterialOverhead_DefaultsToTheCatalogValue()
    {
        Build().ToRun().MaterialOverheadPercent.Should().Be(68m);
    }

    [Fact]
    public void BuildQuery_MaterialOverhead_EnteredValueIsUsed_AndDoesNotChangeTheSql()
    {
        // Overhead is added to the workbook, never to the layer values, so the SQL is identical.
        var entered = Build(("materialOverheadPercent", "72.5"));

        entered.ToRun().MaterialOverheadPercent.Should().Be(72.5m);
        entered.Sql.Should().Be(Build().Sql);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("x")]
    [InlineData("100001")]
    public void BuildQuery_MaterialOverheadOutOfRange_IsRejected(string entered)
    {
        var act = () => Build(("materialOverheadPercent", entered));

        act.Should().Throw<ReportValidationException>().Which.Errors.Single().Should().Contain("Material overhead");
    }

    [Fact]
    public void BuildQuery_ZeroMaterialOverhead_IsAllowed()
    {
        Build(("materialOverheadPercent", "0")).ToRun().MaterialOverheadPercent.Should().Be(0m);
    }

    [Fact]
    public void BuildQuery_NoOverheadParameterInTheCatalog_Throws()
    {
        var act = () => CreateFetcher().BuildQuery(ReportWithout("materialOverheadPercent"), new Dictionary<string, string?>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*materialOverheadPercent*");
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

    // ── Karen's 2026-10-16 revision: the standard-cost lookup ─────────────────────

    [Fact]
    public void BuildQuery_StandardCost_UsesCostType3_AndFallsBackToKocsu4_InBothHalves()
    {
        // Revised lookup: MCHEAD cost type 3 (was 2); KOCSU1 for purchased items, KOCSU4 for
        // finished goods where KOCSU1 is zero. Both halves of the UNION must agree.
        var halves = Build().Sql.Split("UNION ALL");

        halves.Should().HaveCount(2);
        foreach (var half in halves)
        {
            var sql = Regex.Replace(half, @"\s+", " ");
            sql.Should().Contain("KOPCTP = 3");
            sql.Should().NotContain("KOPCTP = 2");
            sql.Should().Contain("COALESCE(NULLIF(KOCSU1, 0), KOCSU4) AS KOCSU1");
            sql.Should().Contain("(KOCSU1 > 0 OR KOCSU4 > 0)");
        }
    }

    [Fact]
    public void BuildQuery_FetchesOneRowBeyondMaxRows()
    {
        Build().Sql.Should().Contain("FETCH FIRST 100001 ROWS ONLY");
    }
}
