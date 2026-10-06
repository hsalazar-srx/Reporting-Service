using System.Xml.Linq;
using FluentAssertions;

namespace Reporting.Tests.Catalog;

/// <summary>
/// config/report-catalog.json lives at the repository root, outside the Reporting.Api project.
/// Unless the project includes it explicitly, `dotnet publish` leaves it out and the server keeps
/// serving whatever catalog was copied there by hand — new reports never appear.
/// </summary>
public sealed class CatalogPublishTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Reporting.Service.sln")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test must run inside the Reporting-Service repository");
        return dir!.FullName;
    }

    [Fact]
    public void BuildOutput_ContainsTheRepositoryCatalog()
    {
        // Content items of the referenced Reporting.Api project are copied into this test's output.
        var built = Path.Combine(AppContext.BaseDirectory, "config", "report-catalog.json");
        var source = Path.Combine(RepoRoot(), "config", "report-catalog.json");

        File.Exists(built).Should().BeTrue("Reporting.Api must ship config/report-catalog.json");
        File.ReadAllText(built).Should().Be(File.ReadAllText(source));
    }

    [Fact]
    public void ApiProject_AlwaysCopiesCatalogOnPublish()
    {
        // "Always", not "PreserveNewest": a newer hand-edited copy in the target folder must not
        // block the catalog from the release being published.
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "src", "Reporting.Api", "Reporting.Api.csproj"));

        var catalog = csproj.Descendants("Content")
            .SingleOrDefault(e => (string?)e.Attribute("Link") == @"config\report-catalog.json");

        catalog.Should().NotBeNull("Reporting.Api.csproj must include the catalog as Content");
        ((string?)catalog!.Attribute("CopyToPublishDirectory")).Should().Be("Always");
    }
}
