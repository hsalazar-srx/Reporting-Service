namespace Reporting.Core.Catalog;

/// <summary>
/// Provides access to the report catalog loaded from report-catalog.json.
/// Uses skill: data/reporting-integration v1.0
/// </summary>
public interface IReportCatalogProvider
{
    /// <summary>
    /// Gets all report definitions. Returns last-known-good catalog if file is temporarily invalid.
    /// </summary>
    Task<IReadOnlyList<ReportDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets report definitions for a specific domain.
    /// </summary>
    Task<IReadOnlyList<ReportDefinition>> GetByDomainAsync(
        string domain,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single report definition by ID. Returns null if not found.
    /// </summary>
    Task<ReportDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// True if the last reload attempt failed and we are serving the last-known-good catalog.
    /// Exposed to health check to return Degraded status.
    /// </summary>
    bool IsServingLastKnownGood { get; }

    /// <summary>
    /// Reason for last reload failure (null if catalog is current).
    /// </summary>
    string? LastReloadFailureReason { get; }
}
