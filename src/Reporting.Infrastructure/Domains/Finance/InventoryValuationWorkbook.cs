using System.Globalization;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Infrastructure.Domains.Finance;

/// <summary>
/// Turns Inventory Valuation layer rows into the four-sheet workbook:
///   1. Summary        — one row per warehouse + item; every figure counted once
///   2. FIFO Layers    — the query rows as returned
///   3. Reconciliation — per warehouse and in total, against Movex WAC and standard cost
///   4. Assumptions    — filters, rates and methodology the file was produced with
///
/// Expects rows ordered warehouse, item, newest layer first — the SQL's ORDER BY — so the first
/// row of each item carries its current WAC.
/// </summary>
public static class InventoryValuationWorkbook
{
    private const string NoHistory = "No history";
    private const string NoRate = "No rate";

    private static readonly string[] SummaryColumns =
    [
        "Warehouse", "Item number", "Description", "Stock on hand", "FIFO layers",
        "Value at acquisition rate", "Value at revaluation rate", "FX revaluation",
        "Current WAC (book rate)", "Movex WAC value", "Standard cost", "Movex standard value", "Value source"
    ];

    private static readonly string[] ReconciliationColumns =
    [
        "Warehouse", "Items", "Stock on hand", "Value at acquisition rate", "Value at revaluation rate",
        "FX revaluation", "Movex WAC value", "Difference vs WAC", "Difference vs WAC %",
        "Movex standard value", "Items without standard cost", "Estimated items"
    ];

    // Rates, unit costs and receipt quantities — numeric, but a total means nothing.
    private static readonly string[] LayerUnitColumns =
    [
        "AUD_NEW_AVG_COST_SPOT_RATE", "ACQUISITION_COST", "AUD_NEW_AVG_COST_BOOK_RATE",
        "SPOT_RATE_AT_ENTRY_DATE", "TRANSACTION_QTY", "CURRENT_SPOT_RATE", "STANDARD_COST"
    ];

    public static ReportDataSet Build(
        string reportId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> layers,
        InventoryValuationRun run,
        DateTime executedAtUtc)
    {
        var items = Summarise(layers);

        return new ReportDataSet
        {
            ReportId = reportId,
            ExecutedAtUtc = executedAtUtc,
            Columns = SummaryColumns,
            Rows = items.Select(SummaryRow).ToList(),
            NonSummableColumns = ["Current WAC (book rate)", "Standard cost"],
            SubReports =
            [
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = "FIFO Layers",
                    Columns = layers.Count > 0 ? layers[0].Keys.ToList() : [],
                    Rows = layers.Select(PresentLayer).ToList(),
                    NonSummableColumns = LayerUnitColumns
                },
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = "Reconciliation",
                    Columns = ReconciliationColumns,
                    Rows = Reconcile(items),
                    NonSummableColumns = ReconciliationColumns       // carries its own total row
                },
                new ReportDataSet
                {
                    ReportId = reportId, ExecutedAtUtc = executedAtUtc, SectionLabel = "Assumptions",
                    Columns = ["Setting", "Value"],
                    Rows = Assumptions(run, executedAtUtc)
                }
            ]
        };
    }

    // DB2 returns Movex dates and times as DECIMAL. Dates become real dates and times whole numbers,
    // otherwise Excel shows 20,261,012.0000 and the renderer adds them up in the TOTAL row.
    private static readonly string[] DateColumns = ["TRANSACTION_DATE", "ENTRY_DATE"];
    private static readonly string[] TimeColumns = ["ENTRY_TIME", "TIME_SUFFIX"];

    private static IReadOnlyDictionary<string, object?> PresentLayer(IReadOnlyDictionary<string, object?> layer)
    {
        var row = new Dictionary<string, object?>(layer);
        foreach (var column in DateColumns.Where(row.ContainsKey))
            row[column] = row[column] is { } v ? MovexDateConverter.FromMovexInt(Convert.ToInt32(v, CultureInfo.InvariantCulture)) : null;
        foreach (var column in TimeColumns.Where(row.ContainsKey))
            row[column] = row[column] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
        return row;
    }

    private sealed record Item(
        string Warehouse, string ItemNumber, string? Description, decimal StockOnHand, int Layers,
        decimal AcquisitionValue, decimal RevaluationValue, decimal CurrentWac, decimal StandardCost,
        string ValueSource)
    {
        public decimal WacValue => StockOnHand * CurrentWac;
        public decimal StandardValue => StockOnHand * StandardCost;
        public bool Estimated => ValueSource.Contains(NoHistory) || ValueSource.Contains(NoRate);
    }

    private static List<Item> Summarise(IReadOnlyList<IReadOnlyDictionary<string, object?>> layers) =>
        layers
            .GroupBy(r => (Wh: Text(r, "WAREHOUSE"), It: Text(r, "ITEM_NO")))   // keeps first-seen order
            .Select(g =>
            {
                var newest = g.First();
                var soh = g.Select(r => r.GetValueOrDefault("SOH_QUANTITY")).FirstOrDefault(v => v is not null);
                return new Item(
                    g.Key.Wh, g.Key.It, Text(newest, "ITEM_DESCRIPTION"),
                    StockOnHand: Dec(soh),
                    Layers: g.Count(),
                    AcquisitionValue: g.Sum(r => Dec(r.GetValueOrDefault("STOCK_VALUE_AT_ACQUISITION_RATE"))),
                    RevaluationValue: g.Sum(r => Dec(r.GetValueOrDefault("STOCK_VALUE_AT_CURRENT_RATE"))),
                    CurrentWac: Dec(newest.GetValueOrDefault("AUD_NEW_AVG_COST_BOOK_RATE")),
                    StandardCost: Dec(newest.GetValueOrDefault("STANDARD_COST")),
                    ValueSource: string.Join(", ", g.Select(r => Text(r, "VALUE_SOURCE")).Distinct()));
            })
            .ToList();

    private static IReadOnlyDictionary<string, object?> SummaryRow(Item i) => new Dictionary<string, object?>
    {
        ["Warehouse"] = i.Warehouse,
        ["Item number"] = i.ItemNumber,
        ["Description"] = i.Description,
        ["Stock on hand"] = i.StockOnHand,
        ["FIFO layers"] = i.Layers,
        ["Value at acquisition rate"] = i.AcquisitionValue,
        ["Value at revaluation rate"] = i.RevaluationValue,
        ["FX revaluation"] = i.RevaluationValue - i.AcquisitionValue,
        ["Current WAC (book rate)"] = i.CurrentWac,
        ["Movex WAC value"] = i.WacValue,
        ["Standard cost"] = i.StandardCost,
        ["Movex standard value"] = i.StandardValue,
        ["Value source"] = i.ValueSource
    };

    private static List<IReadOnlyDictionary<string, object?>> Reconcile(List<Item> items)
    {
        var rows = items.GroupBy(i => i.Warehouse)
            .Select(g => ReconciliationRow(g.Key, g.ToList()))
            .ToList();
        rows.Add(ReconciliationRow("All warehouses", items));
        return rows;
    }

    private static IReadOnlyDictionary<string, object?> ReconciliationRow(string label, List<Item> items)
    {
        var acquisition = items.Sum(i => i.AcquisitionValue);
        var revaluation = items.Sum(i => i.RevaluationValue);
        var wac = items.Sum(i => i.WacValue);
        var difference = acquisition - wac;

        return new Dictionary<string, object?>
        {
            ["Warehouse"] = label,
            ["Items"] = items.Count,
            ["Stock on hand"] = items.Sum(i => i.StockOnHand),
            ["Value at acquisition rate"] = acquisition,
            ["Value at revaluation rate"] = revaluation,
            ["FX revaluation"] = revaluation - acquisition,
            ["Movex WAC value"] = wac,
            ["Difference vs WAC"] = difference,
            ["Difference vs WAC %"] = wac == 0 ? 0m : Math.Round(difference / wac * 100, 2),
            ["Movex standard value"] = items.Sum(i => i.StandardValue),
            ["Items without standard cost"] = items.Count(i => i.StandardCost == 0),
            ["Estimated items"] = items.Count(i => i.Estimated)
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> Assumptions(InventoryValuationRun run, DateTime executedAtUtc)
    {
        var rows = new List<(string, string)>
        {
            ("Report", "Inventory Valuation — FIFO layer unwind of the WAC history (FCAAVP)"),
            ("Methodology", "Karen Lewin's SQL of 2026-10-13, unchanged — pending Finance validation"),
            ("Run at (UTC)", executedAtUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            ("Company", run.Company),
            ("DB2 schema", run.Schema),
            ("Facility", "D"),
            ("Warehouses", string.Join(", ", run.Warehouses)),
            ("Item number filter", run.ItemNumber ?? "All items"),
            ("Item group filter", run.ItemGroup ?? "All item groups"),
            ("Stock included", "Stock status 20 with quantity on hand > 0 (current — not as at a past date)"),
            ("Revaluation rate", run.RevalueAtDate is { } d
                ? $"Latest rate on or before {d:yyyy-MM-dd}"
                : "Latest available"),
            ("Acquisition rate", "Spot rate on or before each layer's WAC recalculation date (A7RGDT)"),
            ("Exchange rates", $"Spot rate type {run.SpotRateType} and book rate type {run.BookRateType}, CCURRA division {run.RateDivision}"),
            ("True AUD formula", "Acquisition cost x book rate / spot rate (AUD items unchanged)"),
        };

        foreach (var rate in run.BookRates)
        {
            var effective = rate.EffectiveFrom is { } e ? $", effective {e:yyyy-MM-dd}" : string.Empty;
            rows.Add(($"Book rate {rate.Currency}",
                $"{rate.Rate.ToString("0.00####", CultureInfo.InvariantCulture)} (CCURRA type {run.BookRateType}{effective})"));
        }

        rows.AddRange(
        [
            ("Note", "Today's book rate is applied to every layer, as in the original SQL. Layers booked under an " +
                     "earlier book rate (e.g. USD before 2019-07-01) get a slightly different correction. Measured once, " +
                     "on 2026-10-14, at about +0.02% of total value; not recomputed on each run."),
            ("Note", "On FIFO Layers, stock on hand and Movex stock value appear on each item's first row only. " +
                     "Use Summary or Reconciliation for totals."),
            ("[CHANGED] from the original", "Part B column order fixed to match Part A (standard cost and Movex value " +
                                            "were landing under ENTRY_TIME / TIME_SUFFIX)"),
            ("[CHANGED] from the original", "MOVEX_STOCK_VALUE shown on each item's first layer row only (it repeated " +
                                            "on every row, so totalling it overstated the value)"),
            ("[CHANGED] from the original", "VALUE_SOURCE column added; book rates read from CCURRA instead of hard-coded"),
        ]);

        return rows.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["Setting"] = r.Item1,
            ["Value"] = r.Item2
        }).ToList();
    }

    private static string Text(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column)?.ToString()?.Trim() ?? string.Empty;

    private static decimal Dec(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
    };
}
