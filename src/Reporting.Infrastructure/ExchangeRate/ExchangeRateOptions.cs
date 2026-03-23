namespace Reporting.Infrastructure.ExchangeRate;

/// <summary>
/// Configuration options for the RBA exchange rate sync service.
/// Bound from "ExchangeRateSync" section in appsettings.json.
/// </summary>
public sealed class ExchangeRateSyncOptions
{
    public const string SectionName = "ExchangeRateSync";

    /// <summary>Enable or disable the background sync service.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Time of day (UTC) to run the daily sync, e.g. "23:00".</summary>
    public string ScheduleTimeUtc { get; set; } = "23:00";

    /// <summary>How often (minutes) the scheduler checks whether it's time to sync.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>RBA CSV endpoint URL.</summary>
    public string RbaUrl { get; set; } =
        "https://www.rba.gov.au/statistics/tables/csv/f11.1-data.csv";

    /// <summary>HTTP timeout in seconds for fetching the RBA CSV.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Skip sync on Saturday and Sunday (RBA doesn't publish on weekends).</summary>
    public bool SkipWeekends { get; set; } = true;

    /// <summary>
    /// How many prior days to look back when querying a rate that doesn't exist for a given date.
    /// Handles weekends and public holidays. Default: 3 (covers Mon→Fri).
    /// </summary>
    public int FallbackDays { get; set; } = 3;

    /// <summary>DB2 command timeout in seconds.</summary>
    public int DbCommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Currency codes to sync. Extend later for MYR, EUR, etc.</summary>
    public List<string> Currencies { get; set; } = ["USD"];

    /// <summary>Parses ScheduleTimeUtc into a TimeOnly value.</summary>
    public TimeOnly ParsedScheduleTime =>
        TimeOnly.TryParse(ScheduleTimeUtc, out var t) ? t : new TimeOnly(23, 0);
}

/// <summary>
/// DB2 connection settings for exchange rate access.
/// Reuses the existing DataSources:Db2 section from appsettings.json.
/// </summary>
public sealed class Db2ExchangeRateOptions
{
    public const string SectionName = "DataSources:Db2";

    /// <summary>ODBC connection string. From User Secrets (dev) or Azure Key Vault (prod).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>DB2 schema containing CCURRA. Default: mvxcdta.</summary>
    public string Schema { get; set; } = "mvxcdta";

    /// <summary>DB2 command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>M3 user ID stamped as CUCHID on inserted records. From DataSources:Db2:MovexUser.</summary>
    public string? MovexUser { get; set; }
}
