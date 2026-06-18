using System.Globalization;

namespace Reporting.Infrastructure.Pipeline;

/// <summary>
/// Converts between MOVEX date integers (YYYYMMDD stored as INT) and .NET DateTime.
///
/// MOVEX stores dates as INT in YYYYMMDD format:
///   20260101 = 2026-01-01
///   0        = null / not set
///
/// Uses skill: integration/movex-db2-data-source v1.0
/// </summary>
public static class MovexDateConverter
{
    /// <summary>
    /// Converts a MOVEX INT date (YYYYMMDD) to DateTime?.
    /// Returns null for 0 or negative values (not set).
    /// </summary>
    public static DateTime? FromMovexInt(int movexDate)
    {
        if (movexDate <= 0)
            return null;

        var str = movexDate.ToString("D8", CultureInfo.InvariantCulture);
        return DateTime.ParseExact(str, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None);
    }

    /// <summary>
    /// Converts a DateTime to a MOVEX INT date (YYYYMMDD).
    /// </summary>
    public static int ToMovexInt(DateTime date)
        => int.Parse(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts a MOVEX INT date to a DB2 positional parameter value (int).
    /// Convenience wrapper for building query parameters.
    /// </summary>
    public static int ToDb2Param(DateTime date) => ToMovexInt(date);

    /// <summary>
    /// Converts a date string (YYYY-MM-DD) from a report parameter to a MOVEX INT.
    /// Throws <see cref="FormatException"/> if the string is not a valid date.
    /// </summary>
    public static int ParseParamToMovexInt(string dateParam)
    {
        var date = DateTime.ParseExact(
            dateParam.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

        return ToMovexInt(date);
    }
}
