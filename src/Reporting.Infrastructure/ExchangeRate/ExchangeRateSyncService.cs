using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reporting.Infrastructure.ExchangeRate.Models;

namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Background service that fetches daily SPOT exchange rates from RBA and writes them to
/// mvxcdta.CCURRA via DB2 ODBC.
///
/// Schedule: Runs once daily at ScheduleTimeUtc (default 23:00 UTC).
/// Weekends: Automatically skips Saturday and Sunday (RBA does not publish on weekends).
/// Pattern: Mirrors JsonReportCatalogProvider — Timer-based with SemaphoreSlim and
///          last-known-good status exposed for health checks.
/// </summary>
public sealed class ExchangeRateSyncService : IDisposable
{
    private readonly ExchangeRateSyncOptions _options;
    private readonly RbaApiClient _rbaClient;
    private readonly ILogger<ExchangeRateSyncService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _checkTimer;

    // Expose sync status for health check
    public SyncStatus LastStatus { get; private set; } = SyncStatus.NotRun;
    public DateTime? LastSyncUtc { get; private set; }
    public string? LastSyncError { get; private set; }
    public string? SkipReason { get; private set; }
    public DateTime? NextScheduledSyncUtc { get; private set; }

    public ExchangeRateSyncService(
        IOptions<ExchangeRateSyncOptions> options,
        RbaApiClient rbaClient,
        Func<Db2ExchangeRateWriter> writerFactory,
        ILogger<ExchangeRateSyncService> logger)
    {
        _options = options.Value;
        _rbaClient = rbaClient;
        _writerFactory = writerFactory;
        _logger = logger;

        NextScheduledSyncUtc = ComputeNextSyncTime();

        // Check every CheckIntervalMinutes whether it's time to run; does NOT fire immediately
        _checkTimer = new Timer(
            _ => _ = RunIfScheduledAsync(),
            null,
            TimeSpan.FromMinutes(_options.CheckIntervalMinutes),
            TimeSpan.FromMinutes(_options.CheckIntervalMinutes));
    }

    private readonly Func<Db2ExchangeRateWriter> _writerFactory;

    /// <summary>
    /// Runs the sync immediately on application startup (unless today is a weekend).
    /// Called by Program.cs after service registration.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Exchange rate sync is disabled via configuration");
            return;
        }

        _logger.LogInformation(
            "Exchange rate sync service starting. Schedule: {Time} UTC daily, Currencies: {Currencies}",
            _options.ScheduleTimeUtc,
            string.Join(", ", _options.Currencies));

        // Run once immediately at startup (catches up after service restart/deploy)
        await SyncExchangeRatesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Timer callback: checks if the scheduled sync time has passed and runs if so.
    /// Only runs once per day even if the timer fires multiple times past the schedule time.
    /// </summary>
    private async Task RunIfScheduledAsync()
    {
        var nowUtc = DateTime.UtcNow;

        if (NextScheduledSyncUtc.HasValue && nowUtc >= NextScheduledSyncUtc.Value)
        {
            await SyncExchangeRatesAsync(CancellationToken.None).ConfigureAwait(false);
            NextScheduledSyncUtc = ComputeNextSyncTime();

            _logger.LogInformation(
                "Next RBA sync scheduled for {NextSync:O}", NextScheduledSyncUtc);
        }
    }

    private async Task SyncExchangeRatesAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;

        // Weekend check — RBA does not publish on Saturday or Sunday
        var todayAest = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("AUS Eastern Standard Time"));

        if (_options.SkipWeekends &&
            (todayAest.DayOfWeek == DayOfWeek.Saturday || todayAest.DayOfWeek == DayOfWeek.Sunday))
        {
            SkipReason = $"Weekend ({todayAest.DayOfWeek})";
            _logger.LogInformation(
                "Skipping RBA exchange rate sync — {DayOfWeek} (RBA closed on weekends)",
                todayAest.DayOfWeek);
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SkipReason = null;
            var syncedCurrencies = new List<string>();
            var errors = new List<string>();

            foreach (var currency in _options.Currencies)
            {
                try
                {
                    var rate = await _rbaClient.FetchLatestRateAsync(currency, ct).ConfigureAwait(false);

                    if (rate is null)
                    {
                        _logger.LogWarning("RBA returned no data for {Currency} — skipping", currency);
                        continue;
                    }

                    var writer = _writerFactory();
                    var inserted = await writer.WriteAsync(rate, ct).ConfigureAwait(false);

                    if (inserted)
                        syncedCurrencies.Add(currency);
                }
                catch (Exception ex)
                {
                    var msg = $"{currency}: {ex.Message}";
                    errors.Add(msg);
                    _logger.LogError(ex, "Failed to sync {Currency} exchange rate", currency);
                }
            }

            LastSyncUtc = DateTime.UtcNow;

            if (errors.Count == 0)
            {
                LastStatus = SyncStatus.Healthy;
                LastSyncError = null;
                _logger.LogInformation(
                    "Exchange rate sync completed. Inserted: [{Inserted}]",
                    string.Join(", ", syncedCurrencies));
            }
            else
            {
                LastStatus = SyncStatus.Degraded;
                LastSyncError = string.Join("; ", errors);
                _logger.LogWarning(
                    "Exchange rate sync completed with errors: {Errors}", LastSyncError);
            }
        }
        catch (Exception ex)
        {
            LastStatus = SyncStatus.Failed;
            LastSyncError = ex.Message;
            _logger.LogError(ex, "Exchange rate sync failed");
        }
        finally
        {
            _lock.Release();
        }
    }

    private DateTime ComputeNextSyncTime()
    {
        var scheduledTime = _options.ParsedScheduleTime;
        var nowUtc = DateTime.UtcNow;
        var todaySchedule = nowUtc.Date.Add(scheduledTime.ToTimeSpan());
        return nowUtc < todaySchedule ? todaySchedule : todaySchedule.AddDays(1);
    }

    public void Dispose()
    {
        _checkTimer.Dispose();
        _lock.Dispose();
        _rbaClient.Dispose();
    }
}

public enum SyncStatus
{
    NotRun,
    Healthy,
    Degraded,
    Failed
}
