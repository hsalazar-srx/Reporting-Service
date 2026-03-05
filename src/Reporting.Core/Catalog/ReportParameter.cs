namespace Reporting.Core.Catalog;

/// <summary>
/// Defines a typed input parameter for a report.
/// </summary>
public sealed record ReportParameter
{
    /// <summary>Internal parameter name (used as key in API requests).</summary>
    public required string Name { get; init; }

    /// <summary>User-facing label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Parameter type: date | string | integer | decimal | multiselect.</summary>
    public required string Type { get; init; }

    /// <summary>Whether the parameter must be supplied by the caller.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Optional default value applied when parameter is omitted.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Allowed values for multiselect parameters.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>Human-readable description shown in Swagger and SM-Portal.</summary>
    public string? Description { get; init; }
}
