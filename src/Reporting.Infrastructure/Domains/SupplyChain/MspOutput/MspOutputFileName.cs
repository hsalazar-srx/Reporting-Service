using System.Globalization;
using System.Text.RegularExpressions;

namespace Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

/// <summary>
/// The name of a file the legacy PHP MSP writes to its Output folder on SRXAPP03:
/// prefix + to date + from date + division + company + optional customer, e.g.
/// <c>MSP2027050320241103D100.xlsx</c> (MSPModel.php:300).
///
/// The from date is the run date minus two years (MSP looks back two years of MOs), and the
/// to date is usually six months ahead. A customer name marks an ad-hoc browser run, not the
/// scheduled daily run.
/// </summary>
public sealed partial record MspOutputFileName(
    string Prefix,
    DateOnly ToDate,
    DateOnly FromDate,
    string Division,
    string Company,
    string? Customer)
{
    /// <summary>Report type for each prefix (MSPModel.php:27).</summary>
    public static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>
    {
        ["MSP"] = "MSP Report",
        ["PRD"] = "Production Plan Detailed Report",
        ["PRS"] = "Production Plan Summary Report",
        ["RRS"] = "Revenue Report",
    };

    /// <summary>True for the scheduled run; false for an ad-hoc run for one customer.</summary>
    public bool IsDailyRun => Customer is null;

    [GeneratedRegex(@"^(?<prefix>[A-Z]{3})(?<to>\d{8})(?<from>\d{8})(?<division>[A-Z])(?<company>\d{3})(?<customer>.*)\.xlsx$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Parses a file name (with or without its folder). False when it isn't an MSP output name.</summary>
    public static bool TryParse(string fileName, out MspOutputFileName? result)
    {
        result = null;
        var match = Pattern().Match(Path.GetFileName(fileName));
        if (!match.Success)
            return false;

        var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
        if (!Prefixes.ContainsKey(prefix))
            return false;

        if (!DateOnly.TryParseExact(match.Groups["to"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)
            || !DateOnly.TryParseExact(match.Groups["from"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            return false;

        var customer = match.Groups["customer"].Value.Trim();
        result = new MspOutputFileName(
            prefix, to, from,
            match.Groups["division"].Value.ToUpperInvariant(),
            match.Groups["company"].Value,
            customer.Length == 0 ? null : customer);
        return true;
    }
}
