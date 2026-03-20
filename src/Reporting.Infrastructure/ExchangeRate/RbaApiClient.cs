using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Reporting.Infrastructure.ExchangeRate.Models;

namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Fetches daily exchange rates from the official RBA Table F11.1 CSV endpoint.
/// URL: https://www.rba.gov.au/statistics/tables/csv/f11.1-data.csv
///
/// Rate convention: 1 {Currency} = {Rate} AUD  (e.g. 1 USD = 0.6828 AUD)
/// RBA publishes on weekdays only. No authentication required (public endpoint).
/// TLS 1.2+ enforced.
/// </summary>
public class RbaApiClient : IDisposable
{
    private const string CsvUrl = "https://www.rba.gov.au/statistics/tables/csv/f11.1-data.csv";
    private const int TimeoutSeconds = 30;

    private readonly HttpClient _httpClient;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly ILogger<RbaApiClient> _logger;

    public RbaApiClient(ILogger<RbaApiClient> logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            // TLS 1.2+ only — workspace security standard
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SM-Reporting-Service/1.0");

        // Retry 3x with exponential backoff: 2s, 4s, 8s
        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .Or<HttpRequestException>()
            .Or<TaskCanceledException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (outcome, timespan, retryCount, _) =>
                    _logger.LogWarning(
                        "RBA fetch attempt {RetryCount}/3 failed ({Reason}). Retrying in {DelayMs}ms",
                        retryCount,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString(),
                        timespan.TotalMilliseconds));
    }

    /// <summary>
    /// Fetches the latest exchange rate for <paramref name="currencyCode"/> from the RBA CSV.
    /// Returns null if the currency is not found in today's data.
    /// </summary>
    /// <param name="currencyCode">ISO 4217 code, e.g. "USD".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ExchangeRateData?> FetchLatestRateAsync(string currencyCode, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(currencyCode, @"^[A-Z]{3}$"))
            throw new ArgumentException($"Invalid currency code: {currencyCode}", nameof(currencyCode));

        _logger.LogInformation("Fetching RBA exchange rate CSV for {Currency}", currencyCode);

        var response = await _retryPolicy.ExecuteAsync(
            async () => await _httpClient.GetAsync(CsvUrl, ct).ConfigureAwait(false))
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var csv = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var rate = ParseRate(csv, currencyCode);

        if (rate is null)
        {
            _logger.LogWarning("Currency {Currency} not found in RBA CSV response", currencyCode);
            return null;
        }

        _logger.LogInformation(
            "RBA rate fetched: 1 {Currency} = {Rate} AUD for {Date}",
            currencyCode, rate.Rate, rate.EffectiveDate);

        return rate;
    }

    /// <summary>
    /// Parses the RBA F11.1 CSV format.
    /// Header row contains currency codes; data rows are Date,Rate1,Rate2,...
    /// The last data row contains the most recent business-day rate.
    /// </summary>
    internal static ExchangeRateData? ParseRate(string csv, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(csv))
            throw new InvalidOperationException("RBA CSV response is empty");

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the header row — use the "Units" row which contains bare ISO codes (USD, GBP...)
        // RBA CSV has several metadata rows before data rows
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("Units,", StringComparison.OrdinalIgnoreCase))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
            throw new InvalidOperationException("Could not locate header row in RBA CSV");

        var headers = SplitCsv(lines[headerIndex]);
        int currencyIndex = -1;
        for (int i = 1; i < headers.Length; i++)
        {
            if (string.Equals(headers[i].Trim(), currencyCode, StringComparison.OrdinalIgnoreCase))
            {
                currencyIndex = i;
                break;
            }
        }

        if (currencyIndex < 0)
            return null;

        // Find the last non-empty data row
        string? lastDataLine = null;
        for (int i = lines.Length - 1; i > headerIndex; i--)
        {
            var trimmed = lines[i].Trim();
            if (!string.IsNullOrEmpty(trimmed) && char.IsDigit(trimmed[0]))
            {
                lastDataLine = trimmed;
                break;
            }
        }

        if (lastDataLine is null)
            throw new InvalidOperationException("No data rows found in RBA CSV");

        var dataColumns = SplitCsv(lastDataLine);

        if (currencyIndex >= dataColumns.Length)
            throw new InvalidOperationException(
                $"Column index {currencyIndex} out of range for data row (has {dataColumns.Length} columns)");

        var dateStr = dataColumns[0].Trim();
        var rateStr = dataColumns[currencyIndex].Trim();

        if (!DateOnly.TryParseExact(dateStr, new[] { "dd-MMM-yyyy", "yyyy-MM-dd", "d/MM/yyyy" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var effectiveDate))
        {
            throw new FormatException($"Cannot parse date '{dateStr}' from RBA CSV");
        }

        if (!decimal.TryParse(rateStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            throw new FormatException($"Cannot parse rate '{rateStr}' for {currencyCode} from RBA CSV");
        }

        if (rate < 0.0001m || rate > 10000m)
            throw new InvalidOperationException(
                $"Rate {rate} for {currencyCode} is outside valid range [0.0001, 10000]");

        return new ExchangeRateData
        {
            Currency = currencyCode.ToUpperInvariant(),
            Rate = rate,
            EffectiveDate = effectiveDate,
            FetchedAtUtc = DateTime.UtcNow,
            UsedFallback = false,
            Source = "RBA:F11.1"
        };
    }

    private static string[] SplitCsv(string line) =>
        line.Split(',');

    public void Dispose() => _httpClient.Dispose();
}
