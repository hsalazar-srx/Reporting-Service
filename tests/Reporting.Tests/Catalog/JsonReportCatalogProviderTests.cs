using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Reporting.Infrastructure.Catalog;

namespace Reporting.Tests.Catalog;

public sealed class JsonReportCatalogProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogPath;

    public JsonReportCatalogProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"reporting-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _catalogPath = Path.Combine(_tempDir, "report-catalog.json");
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private void WriteCatalog(string json) => File.WriteAllText(_catalogPath, json);

    private JsonReportCatalogProvider CreateProvider(TimeSpan? reloadInterval = null)
        => new(_catalogPath, reloadInterval ?? TimeSpan.FromMinutes(5),
               NullLogger<JsonReportCatalogProvider>.Instance);

    // ── Happy-path loading ─────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ValidCatalogFile_LoadsAllReports()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();

        await provider.LoadAsync();

        var reports = await provider.GetAllAsync();
        reports.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByDomainAsync_FiltersByDomainCaseInsensitive()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();
        await provider.LoadAsync();

        var results = await provider.GetByDomainAsync("costmanagement");

        results.Should().HaveCount(1);
        results[0].Id.Should().Be("cost.test-report");
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsReport()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();
        await provider.LoadAsync();

        var report = await provider.GetByIdAsync("cost.test-report");

        report.Should().NotBeNull();
        report!.DisplayName.Should().Be("Test Cost Report");
    }

    [Fact]
    public async Task GetByIdAsync_IdLookupCaseInsensitive_ReturnsReport()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();
        await provider.LoadAsync();

        var report = await provider.GetByIdAsync("COST.TEST-REPORT");

        report.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();
        await provider.LoadAsync();

        var report = await provider.GetByIdAsync("no.such.report");

        report.Should().BeNull();
    }

    [Fact]
    public async Task IsServingLastKnownGood_AfterSuccessfulLoad_IsFalse()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider();

        await provider.LoadAsync();

        provider.IsServingLastKnownGood.Should().BeFalse();
        provider.LastReloadFailureReason.Should().BeNull();
    }

    // ── Fail-fast on startup ───────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsFileNotFoundException()
    {
        using var provider = new JsonReportCatalogProvider(
            Path.Combine(_tempDir, "missing.json"),
            TimeSpan.FromMinutes(5),
            NullLogger<JsonReportCatalogProvider>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_ThrowsException()
    {
        WriteCatalog("this is not json at all");
        using var provider = CreateProvider();

        await Assert.ThrowsAnyAsync<Exception>(() => provider.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_MissingRequiredField_ThrowsInvalidDataException()
    {
        // id is empty string — fails Validate()
        WriteCatalog("""{"reports":[{"id":"","displayName":"Test","domain":"D","dataSource":"Db2Direct"}]}""");
        using var provider = CreateProvider();

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_DuplicateIds_ThrowsInvalidDataException()
    {
        WriteCatalog("""
            {
              "reports": [
                {"id":"cost.dup","displayName":"A","domain":"CostManagement","dataSource":"Db2Direct"},
                {"id":"cost.dup","displayName":"B","domain":"CostManagement","dataSource":"Db2Direct"}
              ]
            }
            """);
        using var provider = CreateProvider();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadAsync());
        ex.Message.Should().Contain("Duplicate");
    }

    // ── Last-known-good fallback (Gap 6) ───────────────────────────────────

    [Fact]
    public async Task BackgroundReload_OnFailure_ServesLastKnownGoodCatalog()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider(TimeSpan.FromMilliseconds(50));
        await provider.LoadAsync();

        // Corrupt the file so the next background reload fails
        File.WriteAllText(_catalogPath, "{ corrupted json }");

        // Wait for several reload cycles
        await Task.Delay(500);

        provider.IsServingLastKnownGood.Should().BeTrue();
        provider.LastReloadFailureReason.Should().NotBeNullOrWhiteSpace();

        // Old catalog still accessible
        var reports = await provider.GetAllAsync();
        reports.Should().HaveCount(2);
    }

    [Fact]
    public async Task BackgroundReload_OnSuccess_ClearsLastKnownGoodFlag()
    {
        WriteCatalog(TwoReportCatalogJson);
        using var provider = CreateProvider(TimeSpan.FromMilliseconds(50));
        await provider.LoadAsync();

        // Corrupt, wait for degraded state
        File.WriteAllText(_catalogPath, "bad json");
        await Task.Delay(300);
        provider.IsServingLastKnownGood.Should().BeTrue();

        // Restore a valid catalog — next reload should clear the flag
        WriteCatalog(TwoReportCatalogJson);
        await Task.Delay(300);

        provider.IsServingLastKnownGood.Should().BeFalse();
        provider.LastReloadFailureReason.Should().BeNull();
    }

    // ── Test catalog JSON ──────────────────────────────────────────────────

    private const string TwoReportCatalogJson = """
        {
          "reports": [
            {
              "id": "cost.test-report",
              "displayName": "Test Cost Report",
              "domain": "CostManagement",
              "dataSource": "Db2Direct"
            },
            {
              "id": "finance.test-report",
              "displayName": "Test Finance Report",
              "domain": "Finance",
              "dataSource": "SqlServerDw"
            }
          ]
        }
        """;
}
