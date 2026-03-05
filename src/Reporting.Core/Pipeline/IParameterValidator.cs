using Reporting.Core.Catalog;

namespace Reporting.Core.Pipeline;

/// <summary>
/// Validates report execution parameters against a report's parameter schema.
/// Uses skill: data/reporting-integration v1.0
/// </summary>
public interface IParameterValidator
{
    /// <summary>
    /// Validates the supplied parameters against the report definition's parameter schema.
    /// Returns an empty list when all parameters are valid.
    /// </summary>
    IReadOnlyList<string> Validate(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters);
}
