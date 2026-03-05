namespace Reporting.Core.Catalog;

/// <summary>
/// Describes a single report in the catalog.
/// Loaded from config/report-catalog.json.
/// </summary>
public sealed record ReportDefinition
{
    /// <summary>Unique dot-notation ID (e.g., "cost.average-cost-snapshot").</summary>
    public required string Id { get; init; }

    /// <summary>User-facing report name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>What the report shows.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Reporting domain: CostManagement | SupplyChainPerformance | Finance |
    /// InventoryManagement | Procurement | Production
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>Subcategory within the domain.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>AD role required (enforced by SM-Portal in Iteration 3).</summary>
    public string RequiredRole { get; init; } = "Reporting_Read";

    /// <summary>Data source: Db2Direct | SqlServerDw | MovexApi.</summary>
    public required string DataSource { get; init; }

    /// <summary>Output formats supported by this report (json | excel | pdf).</summary>
    public IReadOnlyList<string> SupportedFormats { get; init; } = ["json"];

    /// <summary>Default format if caller doesn't specify.</summary>
    public string DefaultFormat { get; init; } = "json";

    /// <summary>Maximum rows returned (safety cap). 0 = unlimited.</summary>
    public int MaxRows { get; init; } = 10_000;

    /// <summary>Per-report execution timeout override. 0 = use global default.</summary>
    public int ExecutionTimeoutSeconds { get; init; } = 0;

    /// <summary>Name of the Crystal Reports report this replaces (for traceability).</summary>
    public string CrystalReportsEquivalent { get; init; } = string.Empty;

    /// <summary>Typed parameters accepted by this report.</summary>
    public IReadOnlyList<ReportParameter> Parameters { get; init; } = [];
}
