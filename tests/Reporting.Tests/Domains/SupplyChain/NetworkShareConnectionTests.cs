using FluentAssertions;
using Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class NetworkShareConnectionTests
{
    [Theory]
    [InlineData(@"\\SRXAPP03\MSPOutput$", @"\\SRXAPP03\MSPOutput$")]
    [InlineData(@"\\SRXAPP03\MSP-test\Output", @"\\SRXAPP03\MSP-test")]
    [InlineData(@"\\SRXAPP03\MSP-test\Output\", @"\\SRXAPP03\MSP-test")]
    [InlineData("//SRXAPP03/MSP-test/Output", @"\\SRXAPP03\MSP-test")]
    public void ShareRoot_ConnectsToTheShareNotTheFolder(string folder, string expected) =>
        NetworkShareConnection.ShareRoot(folder).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\data\msp")]
    [InlineData(@"\\SRXAPP03")]
    [InlineData("SRXAPP03/MSP-test")]
    public void ShareRoot_NotAUncShare_Throws(string folder)
    {
        var act = () => NetworkShareConnection.ShareRoot(folder);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Open_NoUserConfigured_MakesNoConnection(string? user)
    {
        // A developer PC uses its own access; nothing is connected, nothing is checked.
        using var connection = NetworkShareConnection.Open(@"C:\not\even\a\share", user, null);

        connection.Should().NotBeNull();
    }
}
