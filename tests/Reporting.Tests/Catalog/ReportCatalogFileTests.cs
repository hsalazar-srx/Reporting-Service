using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Reporting.Infrastructure.Catalog;
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
