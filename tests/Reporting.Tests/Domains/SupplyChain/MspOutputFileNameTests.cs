using FluentAssertions;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class MspOutputFileNameTests
{
    [Fact]
    public void TryParse_DailyMelbourneRun_ReadsEveryPart()
    {
        var ok = MspOutputFileName.TryParse("MSP2027050320241103D100.xlsx", out var name);

        ok.Should().BeTrue();
        name!.Prefix.Should().Be("MSP");
        name.ToDate.Should().Be(new DateOnly(2027, 5, 3));
        name.FromDate.Should().Be(new DateOnly(2024, 11, 3));
        name.Division.Should().Be("D");
        name.Company.Should().Be("100");
        name.Customer.Should().BeNull();
        name.IsDailyRun.Should().BeTrue();
    }

    [Fact]
    public void TryParse_FullPath_UsesOnlyTheFileName()
    {
        var ok = MspOutputFileName.TryParse(@"\\SRXAPP03\MSP-test\Output\MSP2027042120241021L100.xlsx", out var name);

        ok.Should().BeTrue();
        name!.Division.Should().Be("L");
    }

    [Fact]
    public void TryParse_CustomerRun_IsNotADailyRun()
    {
        var ok = MspOutputFileName.TryParse("MSP2027050320241103D100THERMO FISHER.xlsx", out var name);

        ok.Should().BeTrue();
        name!.Customer.Should().Be("THERMO FISHER");
        name.IsDailyRun.Should().BeFalse();
    }

    [Theory]
    [InlineData("PRD2027050320241103D100.xlsx", "PRD")]
    [InlineData("PRS2027050320241103D100.xlsx", "PRS")]
    public void TryParse_OtherMspReports_ReadsThePrefix(string file, string prefix)
    {
        MspOutputFileName.TryParse(file, out var name).Should().BeTrue();
        name!.Prefix.Should().Be(prefix);
    }

    [Theory]
    [InlineData("PPW Horizontal - WW45_021126.xlsx")]  // the PPW mailer's files share the folder
    [InlineData("XYZ2027050320241103D100.xlsx")]       // unknown prefix
    [InlineData("MSP2027133220241103D100.xlsx")]       // not a date
    [InlineData("MSP2027050320241103D100.csv")]
    public void TryParse_NotAnMspOutputName_ReturnsFalse(string file)
    {
        MspOutputFileName.TryParse(file, out var name).Should().BeFalse();
        name.Should().BeNull();
    }
}
