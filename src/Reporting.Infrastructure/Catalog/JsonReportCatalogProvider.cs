using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reporting.Core.Catalog;

namespace Reporting.Infrastructure.Catalog;

/// <summary>
/// Loads and caches the report catalog from report-catalog.json.
/// Adds last-known-good fallback: on reload failure, serves the previous valid catalog
/// and exposes IsServingLastKnownGood=true so the health check can return Degraded.
///
/// Uses skill: data/reporting-integration v1.0
/// </summary>
public sealed class JsonReportCatalogProvider : IReportCatalogProvider, IDisposable
{
    private readonly string _catalogPath;
    private readonly TimeSpan _reloadInterval;
    private readonly ILogger<JsonReportCatalogProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _reloadTimer;

    private IReadOnlyList<ReportDefinition>? _catalog;
    private IReadOnlyList<ReportDefinition>? _lastKnownGoodCatalog;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    // Gap 6 mitigation: expose reload state to health check
    public bool IsServingLastKnownGood { get; private set; }
    public string? LastReloadFailureReason { get; private set; }

    public JsonReportCatalogProvider(
        string catalogPath,
        TimeSpan reloadInterval,
        ILogger<JsonReportCatalogProvider> logger)
    {
        _catalogPath = catalogPath;
        _reloadInterval = reloadInterval;
        _logger = logger;

        // Background reload timer — does NOT run immediately; LoadAsync() handles startup
        _reloadTimer = new Timer(
            _ => _ = ReloadInBackgroundAsync(),
            null,
            reloadInterval,
            reloadInterval);
    }

    /// <summary>
    /// Loads the catalog on startup. Throws if file is invalid at startup (fail fast).
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definitions = await ParseCatalogFileAsync(cancellationToken).ConfigureAwait(false);
            Validate(definitions);
            _catalog = definitions;
            _lastKnownGoodCatalog = definitions;
            IsServingLastKnownGood = false;
            LastReloadFailureReason = null;
            _logger.LogInformation("Report catalog loaded: {Count} reports", definitions.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ReportDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is not null)
            return _catalog;

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return _catalog!;
    }

    public async Task<IReadOnlyList<ReportDefinition>> GetByDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(r => string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase))
                  .ToList()
                  .AsReadOnly();
    }

    public async Task<ReportDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    // Background reload — on failure, retain last-known-good and mark Degraded
    private async Task ReloadInBackgroundAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var definitions = await ParseCatalogFileAsync(CancellationToken.None).ConfigureAwait(false);
            Validate(definitions);
            _catalog = definitions;
            _lastKnownGoodCatalog = definitions;
            IsServingLastKnownGood = false;
            LastReloadFailureReason = null;
            _logger.LogInformation("Report catalog reloaded: {Count} reports", definitions.Count);
        }
        catch (Exception ex)
        {
            // Gap 6 mitigation: retain last-known-good catalog — do NOT take service down
            if (_lastKnownGoodCatalog is not null)
            {
                _catalog = _lastKnownGoodCatalog;
                IsServingLastKnownGood = true;
                LastReloadFailureReason = ex.Message;
                _logger.LogError(ex,
                    "Report catalog reload failed. Serving last-known-good catalog ({Count} reports). Reason: {Reason}",
                    _lastKnownGoodCatalog.Count, ex.Message);
            }
            else
            {
                _logger.LogCritical(ex, "Report catalog reload failed and no last-known-good catalog exists.");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IReadOnlyList<ReportDefinition>> ParseCatalogFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
            throw new FileNotFoundException($"Report catalog not found: {_catalogPath}");

        var json = await File.ReadAllTextAsync(_catalogPath, cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.Deserialize<CatalogPayload>(json, _jsonOptions)
                      ?? throw new InvalidDataException("Report catalog is empty or invalid JSON.");

        return payload.Reports?.Select(ToReportDefinition).ToList().AsReadOnly()
               ?? (IReadOnlyList<ReportDefinition>)Array.Empty<ReportDefinition>();
    }

    private static void Validate(IReadOnlyList<ReportDefinition> definitions)
    {
        var missing = definitions.Where(r =>
            string.IsNullOrWhiteSpace(r.Id) ||
            string.IsNullOrWhiteSpace(r.DisplayName) ||
            string.IsNullOrWhiteSpace(r.Domain) ||
            string.IsNullOrWhiteSpace(r.DataSource)).ToList();

        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"Reports missing required fields: {string.Join(", ", missing.Select(r => r.Id))}");
        }

        var duplicateIds = definitions
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new InvalidDataException(
                $"Duplicate report IDs: {string.Join(", ", duplicateIds)}");
        }
    }

    private static ReportDefinition ToReportDefinition(ReportPayload p) => new()
    {
        Id = p.Id ?? string.Empty,
        DisplayName = p.DisplayName ?? string.Empty,
        Description = p.Description ?? string.Empty,
        Domain = p.Domain ?? string.Empty,
        Category = p.Category ?? string.Empty,
        RequiredRole = p.RequiredRole ?? "Reporting_Read",
        DataSource = p.DataSource ?? string.Empty,
        SupportedFormats = p.SupportedFormats ?? ["json"],
        DefaultFormat = p.DefaultFormat ?? "json",
        MaxRows = p.MaxRows ?? 10_000,
        ExecutionTimeoutSeconds = p.ExecutionTimeoutSeconds ?? 0,
        CrystalReportsEquivalent = p.CrystalReportsEquivalent ?? string.Empty,
        Parameters = p.Parameters?.Select(ToParameter).ToList().AsReadOnly()
                     ?? (IReadOnlyList<ReportParameter>)Array.Empty<ReportParameter>()
    };

    private static ReportParameter ToParameter(ParameterPayload p) => new()
    {
        Name = p.Name ?? string.Empty,
        DisplayName = p.DisplayName ?? string.Empty,
        Type = p.Type ?? "string",
        Required = p.Required ?? true,
        DefaultValue = p.DefaultValue,
        AllowedValues = p.AllowedValues ?? [],
        Description = p.Description
    };

    public void Dispose()
    {
        _reloadTimer.Dispose();
        _lock.Dispose();
    }

    // JSON deserialization payload models (private)
    private sealed class CatalogPayload
    {
        public List<ReportPayload>? Reports { get; init; }
    }

    private sealed class ReportPayload
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public string? Domain { get; init; }
        public string? Category { get; init; }
        public string? RequiredRole { get; init; }
        public string? DataSource { get; init; }
        public List<string>? SupportedFormats { get; init; }
        public string? DefaultFormat { get; init; }
        public int? MaxRows { get; init; }
        public int? ExecutionTimeoutSeconds { get; init; }
        public string? CrystalReportsEquivalent { get; init; }
        public List<ParameterPayload>? Parameters { get; init; }
    }

    private sealed class ParameterPayload
    {
        public string? Name { get; init; }
        public string? DisplayName { get; init; }
        public string? Type { get; init; }
        public bool? Required { get; init; }
        public string? DefaultValue { get; init; }
        public List<string>? AllowedValues { get; init; }
        public string? Description { get; init; }
    }
}
