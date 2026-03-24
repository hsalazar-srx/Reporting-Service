using System.Data.Odbc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Writes daily SPOT exchange rates to mvxcdta.CCURRA via DB2 ODBC.
/// Strategy: INSERT only — rates are immutable per date.
/// If a rate already exists for the given date, the operation is skipped (no update).
///
/// CRITICAL: Uses positional params (?) not named params (@) — ODBC requirement.
///
/// Fixed M3 values:
///   CUCONO = 100, CUDIVI = 'D', CUCRTP = '99' (SPOT), CULOCD = 'AUD', CUCHID = 'SRXAPI'
/// </summary>
public sealed class Db2ExchangeRateWriter
{
    private readonly ExchangeRateSyncOptions _options;
    private readonly string _connectionString;
    private readonly string _movexUser;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly ILogger<Db2ExchangeRateWriter> _logger;

    public Db2ExchangeRateWriter(
        IOptions<ExchangeRateSyncOptions> options,
        IOptions<Db2ExchangeRateOptions> db2Options,
        ILogger<Db2ExchangeRateWriter> logger)
    {
        _options = options.Value;
        _connectionString = db2Options.Value.ConnectionString
            ?? throw new InvalidOperationException("DB2 connection string for exchange rates is not configured.");
        _movexUser = db2Options.Value.MovexUser
            ?? throw new InvalidOperationException("DB2 Movex user for exchange rates is not configured.");
        _logger = logger;

        // Retry 3x with exponential backoff for transient DB2 connection failures
        _retryPolicy = Policy
            .Handle<OdbcException>()
            .Or<InvalidOperationException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (ex, timespan, retryCount, _) =>
                    _logger.LogWarning(ex,
                        "DB2 write attempt {RetryCount}/3 failed. Retrying in {DelayMs}ms",
                        retryCount, timespan.TotalMilliseconds));
    }

    /// <summary>
    /// Persists <paramref name="rate"/> to mvxcdta.CCURRA.
    /// If a record already exists for this date/currency/company/division, logs and skips.
    /// </summary>
    /// <returns>True if inserted, false if already existed (skipped).</returns>
    public async Task<bool> WriteAsync(ExchangeRateData rate, CancellationToken ct = default)
    {
        var dateInt = ToM3Date(rate.EffectiveDate);   // YYYYMMDD as integer
        var nowInt  = ToM3Date(DateOnly.FromDateTime(DateTime.Now));
        var nowTime = ToM3Time(DateTime.Now);         // HHMMSS as integer

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            await using var conn = new OdbcConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Check if rate already exists for this date
            if (await ExistsAsync(conn, rate.Currency, dateInt, ct).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Rate for {Currency} on {Date} already exists in CCURRA — skipping INSERT",
                    rate.Currency, rate.EffectiveDate);
                return false;
            }

            // INSERT — all 16 fields matching production SQL
            const string sql = """
                INSERT INTO mvxcdta.CCURRA
                (CUCONO, CUDIVI, CUGLOC, CUCUCD, CUCRTP, CUCUTD, CUARAT,
                 CUTXID, CULOCD, CUDMCU, CURAFA, CURGDT, CURGTM, CULMDT, CUCHNO, CUCHID)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """;

            await using var cmd = new OdbcCommand(sql, conn);
            cmd.CommandTimeout = _options.DbCommandTimeoutSeconds;

            cmd.Parameters.Add("?", OdbcType.Int).Value     = 100;              // CUCONO
            cmd.Parameters.Add("?", OdbcType.Char, 1).Value = "D";              // CUDIVI
            cmd.Parameters.Add("?", OdbcType.Char, 1).Value = " ";              // CUGLOC
            cmd.Parameters.Add("?", OdbcType.Char, 3).Value = rate.Currency;    // CUCUCD
            cmd.Parameters.Add("?", OdbcType.Char, 2).Value = "99";             // CUCRTP (SPOT)
            cmd.Parameters.Add("?", OdbcType.Int).Value     = dateInt;          // CUCUTD
            cmd.Parameters.Add("?", OdbcType.Decimal).Value = rate.Rate;        // CUARAT
            cmd.Parameters.Add("?", OdbcType.Int).Value     = 0;                // CUTXID
            cmd.Parameters.Add("?", OdbcType.Char, 3).Value = "AUD";            // CULOCD
            cmd.Parameters.Add("?", OdbcType.Int).Value     = 2;                // CUDMCU
            cmd.Parameters.Add("?", OdbcType.Int).Value     = 4;                // CURAFA
            cmd.Parameters.Add("?", OdbcType.Int).Value     = nowInt;           // CURGDT
            cmd.Parameters.Add("?", OdbcType.Int).Value     = nowTime;          // CURGTM
            cmd.Parameters.Add("?", OdbcType.Int).Value     = nowInt;           // CULMDT
            cmd.Parameters.Add("?", OdbcType.Int).Value     = 1;                // CUCHNO
            cmd.Parameters.Add("?", OdbcType.Char, 10).Value = _movexUser;        // CUCHID

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Inserted CCURRA: 1 {Currency} = {Rate} AUD for {Date} (CUCUTD={DateInt})",
                rate.Currency, rate.Rate, rate.EffectiveDate, dateInt);

            return true;
        }).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(
        OdbcConnection conn, string currency, int dateInt, CancellationToken ct)
    {
        const string sql = """
            SELECT 1 FROM mvxcdta.CCURRA
            WHERE CUCONO = ? AND CUDIVI = ? AND CUCUCD = ? AND CUCRTP = ? AND CUCUTD = ?
            """;

        await using var cmd = new OdbcCommand(sql, conn);
        cmd.Parameters.Add("?", OdbcType.Int).Value     = 100;
        cmd.Parameters.Add("?", OdbcType.Char, 1).Value = "D";
        cmd.Parameters.Add("?", OdbcType.Char, 3).Value = currency;
        cmd.Parameters.Add("?", OdbcType.Char, 2).Value = "99";
        cmd.Parameters.Add("?", OdbcType.Int).Value     = dateInt;

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null && result != DBNull.Value;
    }

    /// <summary>Converts DateOnly to M3 numeric YYYYMMDD format.</summary>
    internal static int ToM3Date(DateOnly date) =>
        date.Year * 10000 + date.Month * 100 + date.Day;

    /// <summary>Converts DateTime time component to M3 numeric HHMMSS format.</summary>
    internal static int ToM3Time(DateTime dt) =>
        dt.Hour * 10000 + dt.Minute * 100 + dt.Second;
}
