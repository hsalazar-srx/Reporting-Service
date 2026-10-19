using FluentAssertions;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Domains.Finance;

namespace Reporting.Tests.Domains.Finance;

/// <summary>
/// Builds the four sheets from FIFO layer rows: Summary (one row per warehouse+item, every figure
/// counted once), FIFO Layers (as queried), Reconciliation (vs Movex WAC and standard cost) and
/// Assumptions (what this file was produced with).
/// </summary>
public sealed class InventoryValuationWorkbookTests
{
    private static readonly DateTime RunAt = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, object?> Layer(
        string wh, string item, decimal? soh, decimal layerQty, decimal acq, decimal cur,
        decimal wacBook, decimal std, decimal? movex, string source, string ccy = "USD") => new()
    {
        ["WAREHOUSE"] = wh, ["ITEM_NO"] = item, ["ITEM_DESCRIPTION"] = $"Desc {item}",
        ["SOH_QUANTITY"] = soh, ["LAYER_QTY"] = layerQty,
        ["STOCK_VALUE_AT_ACQUISITION_RATE"] = acq, ["STOCK_VALUE_AT_CURRENT_RATE"] = cur,
        ["AUD_NEW_AVG_COST_BOOK_RATE"] = wacBook, ["STANDARD_COST"] = std,
        ["MOVEX_STOCK_VALUE"] = movex, ["VALUE_SOURCE"] = source, ["TRN_CURRENCY"] = ccy
    };

    // Rows arrive ordered warehouse, item, newest layer first — as the SQL's ORDER BY returns them.
    private static readonly List<Dictionary<string, object?>> Layers =
    [
        Layer("D01", "A", 10, 6, 60, 66, 9, 5, 50, "Spot rate"),       // newest layer of A
        Layer("D01", "A", null, 4, 40, 42, 8, 5, null, "Spot rate"),   // older layer of A
        Layer("D01", "C", 2, 2, 7, 7, 3.5m, 4, 8, "No history - purchase price"),
        Layer("D27", "B", 3, 3, 30, 30, 10, 0, 0, "AUD", "AUD"),
    ];

    private static readonly InventoryValuationRun Run = new(
        Company: "100", Schema: "MVXCDTA", Warehouses: ["D01", "D27"],
        ItemNumber: null, ItemGroup: null, RevalueAtDate: null,
        BookRates: [new BookRate("EUR", 0.610000m, new DateOnly(2026, 7, 1)), new BookRate("USD", 0.700000m, new DateOnly(2019, 7, 1))],
        RateDivision: "D", SpotRateType: "99", BookRateType: "1");

    private static ReportDataSet Build() =>
        InventoryValuationWorkbook.Build(InventoryValuationFetcher.Id, Layers, Run, RunAt);

    private static ReportDataSet Sheet(string label) => Build().SubReports.Single(s => s.SectionLabel == label);

    private static IReadOnlyDictionary<string, object?> Row(ReportDataSet sheet, string column, object value) =>
        sheet.Rows.Single(r => Equals(r[column], value));

    // ── Structure ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SummaryIsTheFirstSheet_FollowedByLayersReconciliationAssumptions()
    {
        var workbook = Build();

        workbook.ExecutedAtUtc.Should().Be(RunAt);
        workbook.Columns.Should().Contain("Value at acquisition rate");
        workbook.SubReports.Select(s => s.SectionLabel)
            .Should().Equal("FIFO Layers", "Reconciliation", "Assumptions");
    }

    // ── Summary ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_OneRowPerWarehouseAndItem_LayersAddedUp()
    {
        var summary = Build();
        var a = Row(summary, "Item number", "A");

        summary.Rows.Should().HaveCount(3);
        a["Stock on hand"].Should().Be(10m);
        a["FIFO layers"].Should().Be(2);
        a["Value at acquisition rate"].Should().Be(100m);
        a["Value at revaluation rate"].Should().Be(108m);
        a["FX revaluation"].Should().Be(8m);
    }

    [Fact]
    public void Summary_CurrentWacIsFromTheNewestLayer_AndMovexValuesCountedOnce()
    {
        var a = Row(Build(), "Item number", "A");

        a["Current WAC (book rate)"].Should().Be(9m);
        a["Movex WAC value"].Should().Be(90m);
        a["Standard cost"].Should().Be(5m);
        a["Movex standard value"].Should().Be(50m);
    }

    [Fact]
    public void Summary_ValueSourceShowsHowTheItemWasValued()
    {
        Row(Build(), "Item number", "C")["Value source"].Should().Be("No history - purchase price");
    }

    [Fact]
    public void Summary_UnitColumnsAreNotTotalled()
    {
        Build().NonSummableColumns.Should().BeEquivalentTo("Current WAC (book rate)", "Standard cost");
    }

    // ── FIFO Layers ───────────────────────────────────────────────────────────────

    [Fact]
    public void Layers_AreTheQueryRowsAsReturned_RatesAndUnitCostsNotTotalled()
    {
        var layers = Sheet("FIFO Layers");

        layers.Rows.Should().HaveCount(4);
        layers.Columns.Should().Contain("VALUE_SOURCE");
        layers.NonSummableColumns.Should().Contain(["SPOT_RATE_AT_ENTRY_DATE", "CURRENT_SPOT_RATE", "STANDARD_COST"]);
        layers.NonSummableColumns.Should().NotContain(["STOCK_VALUE_AT_ACQUISITION_RATE", "MOVEX_STOCK_VALUE", "LAYER_QTY"]);
    }

    [Fact]
    public void Layers_MovexDatesBecomeRealDates_TimesBecomeWholeNumbers_NoneTotalled()
    {
        // DB2 returns these as DECIMAL. Left alone they show as 20,261,012.0000 and the
        // renderer adds them up in the TOTAL row.
        var layer = Layer("D01", "A", 10, 6, 60, 66, 9, 5, 50, "Spot rate");
        layer["TRANSACTION_DATE"] = 20261012m;
        layer["ENTRY_DATE"] = 20261013m;
        layer["ENTRY_TIME"] = 82545m;
        layer["TIME_SUFFIX"] = 1m;

        var layers = InventoryValuationWorkbook.Build(InventoryValuationFetcher.Id, [layer], Run, RunAt)
            .SubReports.Single(s => s.SectionLabel == "FIFO Layers");
        var row = layers.Rows.Single();

        row["TRANSACTION_DATE"].Should().Be(new DateTime(2026, 10, 12));
        row["ENTRY_DATE"].Should().Be(new DateTime(2026, 10, 13));
        row["ENTRY_TIME"].Should().Be(82545L);
        row["TIME_SUFFIX"].Should().Be(1L);
    }

    [Fact]
    public void Layers_NoHistoryRow_KeepsBlankDatesAndTimes()
    {
        var layer = Layer("D01", "C", 2, 2, 7, 7, 3.5m, 4, 8, "No history - purchase price");
        layer["TRANSACTION_DATE"] = null;
        layer["ENTRY_DATE"] = null;
        layer["ENTRY_TIME"] = null;
        layer["TIME_SUFFIX"] = null;

        var row = InventoryValuationWorkbook.Build(InventoryValuationFetcher.Id, [layer], Run, RunAt)
            .SubReports.Single(s => s.SectionLabel == "FIFO Layers").Rows.Single();

        row["TRANSACTION_DATE"].Should().BeNull();
        row["ENTRY_DATE"].Should().BeNull();
        row["ENTRY_TIME"].Should().BeNull();
    }

    // ── Reconciliation ────────────────────────────────────────────────────────────

    [Fact]
    public void Reconciliation_PerWarehouseAndAllWarehouses()
    {
        var rec = Sheet("Reconciliation");
        var d01 = Row(rec, "Warehouse", "D01");
        var all = Row(rec, "Warehouse", "All warehouses");

        rec.Rows.Should().HaveCount(3);
        d01["Items"].Should().Be(2);
        d01["Value at acquisition rate"].Should().Be(107m);
        d01["Movex WAC value"].Should().Be(97m);                 // 10 x 9 + 2 x 3.5
        d01["Difference vs WAC"].Should().Be(10m);
        d01["Movex standard value"].Should().Be(58m);
        d01["Estimated items"].Should().Be(1);                   // C has no WAC history

        all["Items"].Should().Be(3);
        all["Value at acquisition rate"].Should().Be(137m);
        all["Movex WAC value"].Should().Be(127m);
        all["Items without standard cost"].Should().Be(1);       // B
        all["Difference vs WAC %"].Should().Be(7.87m);           // 10 / 127
    }

    [Fact]
    public void Reconciliation_HasItsOwnTotalRow_SoNothingIsAutoTotalled()
    {
        var rec = Sheet("Reconciliation");

        rec.NonSummableColumns.Should().BeEquivalentTo(rec.Columns);
    }

    // ── Assumptions ───────────────────────────────────────────────────────────────

    [Fact]
    public void Assumptions_RecordFiltersRatesAndMethodology()
    {
        var text = string.Join("\n", Sheet("Assumptions").Rows.Select(r => $"{r["Setting"]}: {r["Value"]}"));

        text.Should().Contain("Company: 100")
            .And.Contain("Warehouses: D01, D27")
            .And.Contain("Revaluation rate: Latest available")
            .And.Contain("Book rate USD: 0.70 (CCURRA type 1, effective 2019-07-01)")
            .And.Contain("Book rate EUR: 0.61 (CCURRA type 1, effective 2026-07-01)")
            .And.Contain("Spot rate type 99")
            .And.Contain("Karen Lewin")
            .And.Contain("16 Oct")
            .And.Contain("Standard cost: MCHEAD cost type 3")
            .And.Contain("[CHANGED]");
    }

    [Fact]
    public void Assumptions_RevalueDate_IsRecorded()
    {
        var run = Run with { RevalueAtDate = new DateOnly(2026, 9, 30) };
        var sheet = InventoryValuationWorkbook.Build(InventoryValuationFetcher.Id, Layers, run, RunAt)
            .SubReports.Single(s => s.SectionLabel == "Assumptions");

        sheet.Rows.Should().Contain(r => Equals(r["Value"], "Latest rate on or before 2026-09-30"));
    }
}
