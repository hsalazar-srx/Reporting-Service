using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.Catalog;
using Reporting.Infrastructure.Domains.Finance;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Tests.Catalog;

/// <summary>
/// Loads the real config/report-catalog.json that ships with the service, so a malformed
/// edit fails here rather than at startup on the server.
/// </summary>
public sealed class ReportCatalogFileTests
{
    private static string CatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reporting.Service.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must run inside the Reporting-Service repository");
        return Path.Combine(dir!.FullName, "config", "report-catalog.json");
    }

    private static async Task<JsonReportCatalogProvider> LoadAsync()
    {
        var provider = new JsonReportCatalogProvider(
            CatalogPath(), TimeSpan.FromHours(1), Mock.Of<ILogger<JsonReportCatalogProvider>>());
        await provider.LoadAsync();
        return provider;
    }

    // ── In-app help (shown beside the report in SM-Portal) ────────────────────────

    [Fact]
    public async Task EveryReport_HasHelp_SoThePortalOffersItConsistently()
    {
        using var provider = await LoadAsync();

        var reports = await provider.GetAllAsync();

        reports.Should().OnlyContain(r => r.Help != null && r.Help.Sections.Count > 0,
            "a Help button on one report and not another looks broken");
    }

    [Fact]
    public async Task EveryHelpSection_HasATitleAndContent_AndEveryItemATermAndText()
    {
        using var provider = await LoadAsync();

        foreach (var report in await provider.GetAllAsync())
        foreach (var section in report.Help!.Sections)
        {
            section.Title.Should().NotBeNullOrWhiteSpace($"{report.Id} has an untitled help section");
            (section.Text is not null || section.Items.Count > 0 || section.Bullets.Count > 0)
                .Should().BeTrue($"{report.Id} / '{section.Title}' is empty");
            section.Items.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Term) && !string.IsNullOrWhiteSpace(i.Text));
            section.Bullets.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b));
        }
    }

    [Fact]
    public async Task InventoryValuation_HelpNamesTheSheetsTheWorkbookActuallyHas()
    {
        // If a sheet is renamed in code, the help must follow — otherwise it describes a sheet that is not there.
        using var provider = await LoadAsync();
        var report = (await provider.GetByIdAsync("finance.inventory-valuation"))!;
        var run = new InventoryValuationRun("100", "MVXCDTA", ["D01"], null, null, null, [], "D", "99", "1");
        var workbook = InventoryValuationWorkbook.Build(report.Id, [], run, DateTime.UtcNow);

        var helped = report.Help!.Sections.Single(s => s.Title == "The four sheets").Items.Select(i => i.Term);

        helped.Should().BeEquivalentTo(
            new[] { report.DisplayName }.Concat(workbook.SubReports.Select(s => s.SectionLabel)));
    }

    [Fact]
    public async Task InventoryValuation_HelpValueSourceLabelsAreTheOnesTheSqlProduces()
    {
        using var provider = await LoadAsync();
        var report = (await provider.GetByIdAsync("finance.inventory-valuation"))!;
        var fetcher = new InventoryValuationFetcher(
            "DSN=test", new Dictionary<string, string> { ["100"] = "MVXCDTA" }, "100",
            new InventoryValuationOptions(), 300, Mock.Of<ILogger<InventoryValuationFetcher>>());
        var sql = fetcher.BuildQuery(report, new Dictionary<string, string?>(), [new BookRate("USD", 0.70m, null)]).Sql;

        var labels = report.Help!.Sections.Single(s => s.Title == "Value source").Items.Select(i => i.Term);

        labels.Should().NotBeEmpty();
        foreach (var label in labels)
            sql.Should().Contain($"'{label}'", "the help describes a label the report never writes");
    }

    [Fact]
    public async Task InventoryItemList_IsRegisteredInInventoryManagementDomain()
    {
        using var provider = await LoadAsync();

        var reports = await provider.GetByDomainAsync("InventoryManagement");

        reports.Should().ContainSingle(r => r.Id == "inventory.item-list");
    }

    [Fact]
    public async Task InventoryItemList_OffersExcelAndJsonButNotPdf()
    {
        using var provider = await LoadAsync();

        var report = await provider.GetByIdAsync("inventory.item-list");

        report!.SupportedFormats.Should().BeEquivalentTo("json", "excel");
        report.DefaultFormat.Should().Be("excel");
    }

    [Fact]
    public async Task InventoryItemList_MaxRowsLeavesHeadroomOverTheRealItemMaster()
    {
        // Measured 2026-10-01: 101,049 rows on CONO 100 and 100,906 on CONO 300 excluding
        // status 90; 159,653 including it. The original cap of 100,000 truncated the report.
        using var provider = await LoadAsync();

        var report = await provider.GetByIdAsync("inventory.item-list");

        report!.MaxRows.Should().BeGreaterThanOrEqualTo(200_000);
    }

    [Fact]
    public async Task InventoryItemList_CompanyHasNoHardcodedDefault()
    {
        // CONO comes from environment config (DataSources:Db2:DefaultCompany), never the catalog.
        using var provider = await LoadAsync();

        var report = await provider.GetByIdAsync("inventory.item-list");
        var company = report!.Parameters.Single(p => p.Name == "companyCode");

        company.DefaultValue.Should().BeNullOrEmpty();
        company.AllowedValues.Should().BeEquivalentTo("100", "300");
    }

    [Fact]
    public async Task InventoryItemList_RunsWithNoParametersSupplied()
    {
        using var provider = await LoadAsync();
        var report = await provider.GetByIdAsync("inventory.item-list");

        var errors = new ParameterValidator().Validate(report!, new Dictionary<string, string?>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task InventoryItemList_RejectsUnknownCompany()
    {
        using var provider = await LoadAsync();
        var report = await provider.GetByIdAsync("inventory.item-list");

        var errors = new ParameterValidator().Validate(report!,
            new Dictionary<string, string?> { ["companyCode"] = "400" });

        errors.Should().ContainSingle().Which.Should().Contain("companyCode");
    }
}
