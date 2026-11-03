using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

/// <summary>Outcome of asking the buyer view sync to run.</summary>
public enum BuyerViewSyncState { Disabled, InProgress, Completed, Failed }

/// <summary>What the last (or this) sync did, for the API and the log.</summary>
public sealed record BuyerViewSyncStatus(
    BuyerViewSyncState State,
    DateTime? LastRunUtc,
    IReadOnlyList<SyncRunResult> Results,
    string? Error);

/// <summary>
/// Runs <see cref="BuyerViewSync"/> on a timer and on demand.
///
/// Timer: every CheckIntervalMinutes, inside the WindowStart–WindowEnd window (server local
/// time), so each morning's MSP file is applied soon after it lands; checks after that are
/// skipped by the sync itself ("Already applied").
///
/// On demand: <see cref="RunNowAsync"/>, called by POST /api/v1/supplychain/buyer-view/sync from
/// a Windows scheduled task on SRXWEBAPP1. That is the safety net for the in-process timer, which
/// only runs while the IIS worker is alive (the exchange-rate sync's timer was silently dead for
/// four months in 2026 for that reason).
///
/// Off unless BuyerViewSync:Enabled is true. Only one sync runs at a time.
/// Pattern: mirrors ExchangeRateSyncService (Timer + SemaphoreSlim + last status).
/// </summary>
public sealed class BuyerViewSyncService : IDisposable
{
    private readonly BuyerViewSyncOptions _options;
    private readonly Func<IReadOnlyList<SyncRunResult>> _runSync;
    private readonly TimeProvider _clock;
    private readonly ILogger<BuyerViewSyncService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ITimer? _timer;

    public BuyerViewSyncStatus LastStatus { get; private set; }

    public bool Enabled => _options.Enabled;

    public BuyerViewSyncService(
        BuyerViewSyncOptions options,
        Func<IReadOnlyList<SyncRunResult>> runSync,
        TimeProvider clock,
        ILogger<BuyerViewSyncService> logger)
    {
        _options = options;
        _runSync = runSync;
        _clock = clock;
        _logger = logger;
        LastStatus = new BuyerViewSyncStatus(
            options.Enabled ? BuyerViewSyncState.Completed : BuyerViewSyncState.Disabled, null, [], null);

        if (!options.Enabled)
        {
            _logger.LogInformation("[BuyerViewSync] Disabled via configuration (BuyerViewSync:Enabled=false)");
            return;
        }

        var interval = TimeSpan.FromMinutes(options.CheckIntervalMinutes);
        _timer = clock.CreateTimer(_ => _ = RunIfDueAsync(), null, interval, interval);
        _logger.LogInformation(
            "[BuyerViewSync] Enabled: divisions {Divisions}, every {Minutes} min between {Start} and {End}, from {Folder}",
            string.Join(",", options.Divisions), options.CheckIntervalMinutes, options.WindowStart, options.WindowEnd,
            options.OutputFolder);
    }

    /// <summary>True when <paramref name="localTime"/> falls inside the sync window (inclusive).</summary>
    public static bool IsInWindow(TimeOnly localTime, string windowStart, string windowEnd) =>
        localTime >= TimeOnly.Parse(windowStart, CultureInfo.InvariantCulture)
        && localTime <= TimeOnly.Parse(windowEnd, CultureInfo.InvariantCulture);

    private async Task RunIfDueAsync()
    {
        if (!IsInWindow(TimeOnly.FromDateTime(_clock.GetLocalNow().DateTime), _options.WindowStart, _options.WindowEnd))
            return;
        await RunNowAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the sync now, unless it is disabled or already running. Never throws: a failure is
    /// returned (and kept in <see cref="LastStatus"/>) so the caller and the log both see it.
    /// </summary>
    public async Task<BuyerViewSyncStatus> RunNowAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return LastStatus with { State = BuyerViewSyncState.Disabled };

        if (!await _lock.WaitAsync(0, ct).ConfigureAwait(false))
            return LastStatus with { State = BuyerViewSyncState.InProgress };

        try
        {
            var startedUtc = _clock.GetUtcNow().UtcDateTime;
            try
            {
                var results = await Task.Run(_runSync, ct).ConfigureAwait(false);
                var state = results.Any(r => r.Result == SyncRunResult.Failed)
                    ? BuyerViewSyncState.Failed
                    : BuyerViewSyncState.Completed;
                LastStatus = new BuyerViewSyncStatus(state, startedUtc, results, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Store or folder unreachable: keep the service alive, report it, retry next check.
                _logger.LogError(ex, "[BuyerViewSync] Sync failed");
                LastStatus = new BuyerViewSyncStatus(BuyerViewSyncState.Failed, startedUtc, [], ex.Message);
            }
            return LastStatus;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _lock.Dispose();
    }
}
