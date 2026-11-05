using System.Globalization;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Domains.SupplyChain;

/// <summary>
/// Turns PO headers into the five-sheet workbook:
///   1. POs Issued by Buyer — buyer x week, POs issued, with a Total and a weekly average
///   2. PO Lines by Week    — the same, counting PO lines
///   3. Daily               — one row per day, division and buyer: issued, lines, created
///   4. PO Detail           — one row per PO, so every count can be checked
///   5. Definitions         — what is counted, the period and the filters
///
/// "Issued" is the PO's first printout date. A PO counts in the week (Monday to Sunday) it was issued.
/// </summary>
public static class PoIssuedByBuyerWorkbook
{
    public const string LinesSheet = "PO Lines by Week";
    public const string DailySheet = "Daily";
    public const string DetailSheet = "PO Detail";
    public const string DefinitionsSheet = "Definitions";

    public const string AllBuyers = "All buyers";
    public const string AllDivisions = "All divisions";
    public const string NoBuyer = "(no buyer)";
    public const string Total = "Total";
    public const string WeeklyAverage = "Weekly average (full weeks)";

    private static readonly string[] DailyColumns =
        ["Date", "Division", "Buyer", "POs issued", "PO lines issued", "POs created"];

    private static readonly string[] DetailColumns =
    [
        "Division", "Warehouse", "PO number", "Order type", "Buyer", "Supplier", "Entry date",
        "First printout date", "PO lines", "Lowest status", "Highest status", "Issued in period"
    ];

    public static ReportDataSet Build(
        string reportId,
        string title,
        IReadOnlyList<PurchaseOrderHeader> orders,
        PoIssuedRun run,
        DateTime executedAtUtc)
    {
        var weeks = Weeks(run.From, run.To);
        var issued = orders.Where(o => IsIn(o.FirstPrintoutDate, run)).ToList();
        var matrixColumns = new[] { "Division", "Buyer" }
            .Concat(weeks.Select(w => w.Label))
            .Concat([Total, WeeklyAverage])
            .ToList();

        return new ReportDataSet
        {
            ReportId = reportId,
            ExecutedAtUtc = executedAtUtc,
            SectionLabel = title,
            Columns = matrixColumns,
            Rows = Matrix(issued, weeks, _ => 1),
            NonSummableColumns = [WeeklyAverage],
            SubReports =
            [
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = LinesSheet,
                    Columns = matrixColumns,
                    Rows = Matrix(issued, weeks, o => o.Lines),
                    NonSummableColumns = [WeeklyAverage]
                },
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = DailySheet,
                    Columns = DailyColumns,
                    Rows = Daily(orders, run)
                },
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = DetailSheet,
                    Columns = DetailColumns,
                    Rows = orders.Select(o => Detail(o, run)).ToList()
                },
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = DefinitionsSheet,
                    Columns = ["Setting", "Value"],
                    Rows = Definitions(run, weeks, executedAtUtc)
                }
            ]
        };
    }

    /// <summary>A Monday-to-Sunday week in the period. Partial when the period starts or ends inside it.</summary>
    public sealed record Week(DateTime Monday, bool IsPartial)
    {
        public string Label => $"w/c {Monday:yyyy-MM-dd}" + (IsPartial ? " (part)" : string.Empty);
    }

    public static IReadOnlyList<Week> Weeks(DateTime from, DateTime to)
    {
        var weeks = new List<Week>();
        for (var monday = PoIssuedByBuyerFetcher.WeekStart(from); monday <= to.Date; monday = monday.AddDays(7))
            weeks.Add(new Week(monday, IsPartial: monday < from.Date || monday.AddDays(6) > to.Date));
        return weeks;
    }

    // One row per division + buyer, then an "All buyers" row per division, and an "All divisions" row
    // when there is more than one. Weeks with nothing issued show 0 rather than a blank.
    private static List<IReadOnlyDictionary<string, object?>> Matrix(
        List<PurchaseOrderHeader> issued, IReadOnlyList<Week> weeks, Func<PurchaseOrderHeader, int> measure)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var divisions = issued.Select(o => o.Division).Distinct().Order(StringComparer.Ordinal).ToList();

        foreach (var division in divisions)
        {
            var inDivision = issued.Where(o => o.Division == division).ToList();
            foreach (var buyer in inDivision.Select(BuyerOf).Distinct().Order(StringComparer.Ordinal))
                rows.Add(MatrixRow(division, buyer, inDivision.Where(o => BuyerOf(o) == buyer), weeks, measure));
            rows.Add(MatrixRow(division, AllBuyers, inDivision, weeks, measure));
        }

        if (divisions.Count > 1)
            rows.Add(MatrixRow(AllDivisions, AllBuyers, issued, weeks, measure));

        return rows;
    }

    private static IReadOnlyDictionary<string, object?> MatrixRow(
        string division, string buyer, IEnumerable<PurchaseOrderHeader> orders, IReadOnlyList<Week> weeks,
        Func<PurchaseOrderHeader, int> measure)
    {
        var byWeek = orders
            .GroupBy(o => PoIssuedByBuyerFetcher.WeekStart(o.FirstPrintoutDate!.Value))
            .ToDictionary(g => g.Key, g => g.Sum(measure));

        var row = new Dictionary<string, object?> { ["Division"] = division, ["Buyer"] = buyer };
        foreach (var week in weeks)
            row[week.Label] = byWeek.GetValueOrDefault(week.Monday);

        row[Total] = byWeek.Values.Sum();

        var fullWeeks = weeks.Where(w => !w.IsPartial).ToList();
        row[WeeklyAverage] = fullWeeks.Count == 0
            ? null
            : Math.Round((decimal)fullWeeks.Sum(w => byWeek.GetValueOrDefault(w.Monday)) / fullWeeks.Count, 1);
        return row;
    }

    private static List<IReadOnlyDictionary<string, object?>> Daily(IReadOnlyList<PurchaseOrderHeader> orders, PoIssuedRun run)
    {
        var issued = orders.Where(o => IsIn(o.FirstPrintoutDate, run))
            .Select(o => (Date: o.FirstPrintoutDate!.Value.Date, o.Division, Buyer: BuyerOf(o), Issued: 1, o.Lines, Created: 0));
        var created = orders.Where(o => IsIn(o.EntryDate, run))
            .Select(o => (Date: o.EntryDate!.Value.Date, o.Division, Buyer: BuyerOf(o), Issued: 0, Lines: 0, Created: 1));

        return issued.Concat(created)
            .GroupBy(e => (e.Date, e.Division, e.Buyer))
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => g.Key.Division, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Buyer, StringComparer.Ordinal)
            .Select(g => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["Date"] = g.Key.Date,
                ["Division"] = g.Key.Division,
                ["Buyer"] = g.Key.Buyer,
                ["POs issued"] = g.Sum(e => e.Issued),
                ["PO lines issued"] = g.Sum(e => e.Lines),
                ["POs created"] = g.Sum(e => e.Created)
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, object?> Detail(PurchaseOrderHeader o, PoIssuedRun run) =>
        new Dictionary<string, object?>
        {
            ["Division"] = o.Division,
            ["Warehouse"] = o.Warehouse,
            ["PO number"] = o.PoNumber,
            ["Order type"] = o.OrderType,
            ["Buyer"] = BuyerOf(o),
            ["Supplier"] = o.Supplier,
            ["Entry date"] = o.EntryDate,
            ["First printout date"] = o.FirstPrintoutDate,
            ["PO lines"] = o.Lines,
            ["Lowest status"] = o.LowestStatus,
            ["Highest status"] = o.HighestStatus,
            ["Issued in period"] = IsIn(o.FirstPrintoutDate, run) ? "Yes" : "No"
        };

    private static List<IReadOnlyDictionary<string, object?>> Definitions(
        PoIssuedRun run, IReadOnlyList<Week> weeks, DateTime executedAtUtc)
    {
        var fullWeeks = weeks.Count(w => !w.IsPartial);
        var rows = new List<(string, string)>
        {
            ("Report", "POs issued by buyer, per week and per day, from the MOVEX PO header (MPHEAD)"),
            ("Run at (UTC)", executedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("Company", run.Company),
            ("DB2 schema", run.Schema),
            ("Period", $"{run.From:yyyy-MM-dd} to {run.To:yyyy-MM-dd} ({weeks.Count} weeks shown, {fullWeeks} of them full)"),
            ("Divisions", string.Join(", ", run.Divisions) + " (D = Melbourne, L = Johor Bahru)"),
            ("Order types", string.Join(", ", run.OrderTypes)),
            ("Issued", "A PO counts as issued on its first printout date (IALEDT), when it was first sent to the supplier."),
            ("Created", "The PO's entry date in MOVEX (IARGDT). Shown on Daily and PO Detail only."),
            ("Week", "Monday to Sunday. A week marked (part) is cut by the start or end of the period."),
            (WeeklyAverage, "The total over full weeks only, divided by the number of full weeks. Partial weeks are left out so they don't pull the average down."),
            ("Buyer", "The buyer on the PO header (IABUYE). A blank buyer is shown as " + NoBuyer + "."),
            ("PO lines", "The number of lines on the PO header (IANOLN). It matched the PO line table for 95% of POs when checked on 23 Oct 2026."),
            ("Left out", "Deleted POs (lowest status 00). Completed and closed POs stay in: they were still issued."),
            ("PO Detail", "Every PO issued or created in the period. 'Issued in period' says which ones the counts include."),
            ("Note", "Buyer MMRO (Melbourne, PO numbers starting with P) is maintenance and non-production buying."),
        };

        return rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Setting"] = r.Item1,
            ["Value"] = r.Item2
        }).ToList();
    }

    private static bool IsIn(DateTime? date, PoIssuedRun run) =>
        date is { } d && d.Date >= run.From.Date && d.Date <= run.To.Date;

    private static string BuyerOf(PurchaseOrderHeader o) =>
        string.IsNullOrWhiteSpace(o.Buyer) ? NoBuyer : o.Buyer;
}
