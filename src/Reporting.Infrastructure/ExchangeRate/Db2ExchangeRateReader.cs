using System.Data.Odbc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Reads daily SPOT exchange rates from mvxcdta.CCURRA via DB2 ODBC.
///
/// Weekend/holiday fallback: if no rate exists for the requested date,
/// looks back up to FallbackDays (default 5) days to handle weekends and AU public holidays.
/// Returns UsedFallback=true when a prior-day rate is returned.
///
/// CRITICAL: Uses positional params (?) not named params (@) — ODBC requirement.
/// </summary>
public class Db2ExchangeRateReader
{
    private readonly string _connectionString;
    private readonly int _fallbackDays;
    private readonly int _commandTimeoutSeconds;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly ILogger<Db2ExchangeRateReader> _logger;

    public Db2ExchangeRateReader(
        IOptions<ExchangeRateSyncOptions> options,
        IOptions<Db2ExchangeRateOptions> db2Options,
        ILogger<Db2ExchangeRateReader> logger)
    {
        _connectionString = db2Options.Value.ConnectionString
            ?? throw new InvalidOperationException("DB2 connection string for exchange rates is not configured.");
        _fallbackDays = options.Value.FallbackDays;
        _commandTimeoutSeconds = options.Value.DbCommandTimeoutSeconds;
        _logger = logger;

        _retryPolicy = Policy
            .Handle<OdbcException>()
            .Or<InvalidOperationException>()
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(attempt * 2),
                onRetry: (ex, _, retryCount, _) =>
                    _logger.LogWarning(ex, "DB2 read attempt {RetryCount}/2 failed", retryCount));
    }

    /// <summary>
    /// Retrieves the SPOT rate for <paramref name="currency"/> on <paramref name="date"/>.
    /// Falls back to the most recent prior weekday rate if none exists on the exact date.
    /// </summary>
    /// <exception cref="ExchangeRateNotFoundException">
    /// When no rate is found within the fallback window.
    /// </exception>
    public virtual async Task<ExchangeRateData> GetRateForDateAsync(
        string currency, DateOnly date, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(currency, @"^[A-Z]{3}$"))
            throw new ArgumentException($"Invalid currency code: {currency}", nameof(currency));

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            await using var conn = new OdbcConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Try exact date first
            var rate = await QueryRateAsync(conn, currency, date, ct).ConfigureAwait(false);
            if (rate is not null)
                return new ExchangeRateData
                {
                    Currency = currency,
                    Rate = rate.Value,
                    EffectiveDate = date,
                    FetchedAtUtc = DateTime.UtcNow,
                    UsedFallback = false
                };

            // Fallback: look back up to FallbackDays days (handles weekends, public holidays)
            for (int i = 1; i <= _fallbackDays; i++)
            {
                var fallbackDate = date.AddDays(-i);
                rate = await QueryRateAsync(conn, currency, fallbackDate, ct).ConfigureAwait(false);

                if (rate is not null)
                {
                    _logger.LogWarning(
                        "No rate for {Currency} on {Requested}. Using fallback from {Fallback} (offset -{Days}d)",
                        currency, date, fallbackDate, i);

                    return new ExchangeRateData
                    {
                        Currency = currency,
                        Rate = rate.Value,
                        EffectiveDate = fallbackDate,
                        FetchedAtUtc = DateTime.UtcNow,
                        UsedFallback = true,
                        FallbackFromDate = fallbackDate
                    };
                }
            }

            throw new ExchangeRateNotFoundException(
                $"No SPOT rate found for {currency} on {date} or previous {_fallbackDays} days");

        }).ConfigureAwait(false);
    }

    private async Task<decimal?> QueryRateAsync(
        OdbcConnection conn, string currency, DateOnly date, CancellationToken ct)
    {
        var dateInt = Db2ExchangeRateWriter.ToM3Date(date);

        const string sql = """
            SELECT CUARAT FROM mvxcdta.CCURRA
            WHERE CUCONO = ? AND CUDIVI = ? AND CUCUCD = ? AND CUCRTP = ? AND CUCUTD = ?
            """;

        await using var cmd = new OdbcCommand(sql, conn);
        cmd.CommandTimeout = _commandTimeoutSeconds;

        cmd.Parameters.Add("?", OdbcType.Int).Value     = 100;
        cmd.Parameters.Add("?", OdbcType.Char, 1).Value = "D";
        cmd.Parameters.Add("?", OdbcType.Char, 3).Value = currency;
        cmd.Parameters.Add("?", OdbcType.Char, 2).Value = "99";
        cmd.Parameters.Add("?", OdbcType.Int).Value     = dateInt;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null || result == DBNull.Value)
            return null;

        return Convert.ToDecimal(result);
    }
}

/// <summary>Thrown when no exchange rate can be found within the fallback window.</summary>
public sealed class ExchangeRateNotFoundException : Exception
{
    public ExchangeRateNotFoundException(string message) : base(message) { }
}
