using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Pipeline;

/// <summary>
/// Validates report execution parameters against the report's declared parameter schema.
/// Checks required fields, type formats, and allowed-value constraints.
/// Uses skill: data/reporting-integration v1.0
/// </summary>
public sealed class ParameterValidator : IParameterValidator
{
    public IReadOnlyList<string> Validate(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters)
    {
        var errors = new List<string>();

        foreach (var param in report.Parameters)
        {
            parameters.TryGetValue(param.Name, out var value);
            var hasValue = !string.IsNullOrWhiteSpace(value);

            if (!hasValue)
            {
                if (param.Required && string.IsNullOrWhiteSpace(param.DefaultValue))
                    errors.Add($"Parameter '{param.Name}' is required.");
                continue;
            }

            ValidateType(param, value!, errors);
            ValidateAllowedValues(param, value!, errors);
        }

        return errors.AsReadOnly();
    }

    private static void ValidateType(ReportParameter param, string value, List<string> errors)
    {
        switch (param.Type.ToLowerInvariant())
        {
            case "date":
                if (!DateTime.TryParse(value, out _))
                    errors.Add($"Parameter '{param.Name}' must be a valid date (e.g., 2026-01-01).");
                break;

            case "integer":
                if (!int.TryParse(value, out _))
                    errors.Add($"Parameter '{param.Name}' must be a valid integer.");
                break;

            case "decimal":
                if (!decimal.TryParse(value, out _))
                    errors.Add($"Parameter '{param.Name}' must be a valid decimal number.");
                break;
        }
    }

    private static void ValidateAllowedValues(ReportParameter param, string value, List<string> errors)
    {
        if (param.AllowedValues.Count == 0)
            return;

        if (!param.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            errors.Add(
                $"Parameter '{param.Name}' value '{value}' is not allowed. " +
                $"Allowed values: [{string.Join(", ", param.AllowedValues)}].");
    }
}
