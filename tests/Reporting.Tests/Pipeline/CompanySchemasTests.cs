using FluentAssertions;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Tests.Pipeline;

/// <summary>
/// Company → DB2 schema resolution shared by the DB2 report fetchers
/// (CONO 100 = MVXCDTA, CONO 300 = MVXC300).
/// </summary>
public sealed class CompanySchemasTests
{
    private static readonly Dictionary<string, string> Map = new() { ["100"] = "MVXCDTA", ["300"] = "MVXC300" };

    [Theory]
    [InlineData(null, "300", "MVXC300")]
    [InlineData("", "300", "MVXC300")]
    [InlineData(" 100 ", "100", "MVXCDTA")]
    public void Resolve_BlankUsesDefault_OtherwiseTheRequestedCompany(string? requested, string company, string schema)
    {
        var resolved = new CompanySchemas(Map, "300").Resolve(requested);

        resolved.Should().Be((company, schema));
    }

    [Fact]
    public void Resolve_UnknownCompany_Throws()
    {
        var act = () => new CompanySchemas(Map, "300").Resolve("400");

        act.Should().Throw<ArgumentException>().WithMessage("*400*");
    }

    [Fact]
    public void DefaultCompanyWithoutSchema_Throws()
    {
        var act = () => new CompanySchemas(Map, "400");

        act.Should().Throw<InvalidOperationException>().WithMessage("*400*");
    }

    [Fact]
    public void SchemaThatIsNotAPlainIdentifier_Throws()
    {
        var act = () => new CompanySchemas(new Dictionary<string, string> { ["300"] = "MVXC300; DROP TABLE X" }, "300");

        act.Should().Throw<InvalidOperationException>().WithMessage("*schema*");
    }

    [Fact]
    public void CompanyCodeThatIsNotAllDigits_Throws()
    {
        // Company codes are used as SQL literals (CONO is numeric), so they must be digits only.
        var act = () => new CompanySchemas(new Dictionary<string, string> { ["100 OR 1=1"] = "MVXCDTA" }, "100 OR 1=1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*digits*");
    }
}
