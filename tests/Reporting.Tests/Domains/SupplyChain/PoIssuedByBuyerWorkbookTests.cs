using FluentAssertions;
using Reporting.Infrastructure.Domains.SupplyChain;

namespace Reporting.Tests.Domains.SupplyChain;

/// <summary>
/// The counts are the point of the report, so they are tested on a small known set of POs:
/// issued vs created, week bucketing, the per-division and all-division rows, and the weekly average.
/// </summary>
public sealed class PoIssuedByBuyerWorkbookTests
{
    // Thursday 1 Oct to Monday 19 Oct 2026: partial weeks at both ends (w/c 28 Sep, w/c 19 Oct) and
    // two full weeks between them.
    private static readonly PoIssuedRun Run = new("100", "MVXCDTA",
        new DateTime(2026, 10, 1), new DateTime(2026, 10, 19), ["D", "L"], ["010"]);

    private static PurchaseOrderHeader Po(string division, string buyer, string issued, int lines = 1,
        string? created = null, string number = "1") =>
        new(division, division + "01", number, "010", buyer, "SUP", DateTime.Parse(created ?? issued),
            issued == "" ? null : DateTime.Parse(issued), lines, "20", "35");

    private static readonly PurchaseOrderHeader[] Orders =
    [
        Po("D", "BKOOI", "2026-10-02", lines: 2),          // w/c 28 Sep (part)
        Po("D", "BKOOI", "2026-10-06", lines: 1),          // w/c 5 Oct
        Po("D", "BKOOI", "2026-10-07", lines: 3),          // w/c 5 Oct
        Po("D", "KLCHONG", "2026-10-14", lines: 1),        // w/c 12 Oct
        Po("L", "KTLOH", "2026-10-13", lines: 4),          // w/c 12 Oct
        Po("L", "KTLOH", "", created: "2026-10-15"),       // created, never printed: not issued
        Po("D", "BKOOI", "2026-09-30", created: "2026-10-03") // issued before the period
    ];

    private static Reporting.Core.Pipeline.ReportDataSet Build() =>
        PoIssuedByBuyerWorkbook.Build(PoIssuedByBuyerFetcher.Id, "POs Issued by Buyer", Orders, Run, DateTime.UtcNow);

    private static IReadOnlyDictionary<string, object?> Row(
        Reporting.Core.Pipeline.ReportDataSet sheet, string division, string buyer) =>
        sheet.Rows.Single(r => (string)r["Division"]! == division && (string)r["Buyer"]! == buyer);

    [Fact]
    public void Weeks_MarkThePeriodsCutWeeksAsPart()
    {
        var weeks = PoIssuedByBuyerWorkbook.Weeks(Run.From, Run.To);

        weeks.Select(w => w.Label).Should().Equal(
            "w/c 2026-09-28 (part)", "w/c 2026-10-05", "w/c 2026-10-12", "w/c 2026-10-19 (part)");
    }

    [Fact]
    public void PosSheet_CountsPosIssuedPerBuyerPerWeek()
    {
        var bkooi = Row(Build(), "D", "BKOOI");

        bkooi["w/c 2026-09-28 (part)"].Should().Be(1);   // 2 Oct; the 30 Sep PO is before the period
        bkooi["w/c 2026-10-05"].Should().Be(2);
        bkooi["w/c 2026-10-12"].Should().Be(0);
        bkooi[PoIssuedByBuyerWorkbook.Total].Should().Be(3);
    }

    [Fact]
    public void PosSheet_WeeklyAverage_UsesFullWeeksOnly()
    {
        // Full weeks: w/c 5 Oct (2) and w/c 12 Oct (0) → 1.0. The partial first week is ignored.
        Row(Build(), "D", "BKOOI")[PoIssuedByBuyerWorkbook.WeeklyAverage].Should().Be(1.0m);
    }

    [Fact]
    public void PosSheet_HasAnAllBuyersRowPerDivision_AndAnAllDivisionsRow()
    {
        var sheet = Build();

        Row(sheet, "D", PoIssuedByBuyerWorkbook.AllBuyers)[PoIssuedByBuyerWorkbook.Total].Should().Be(4);
        Row(sheet, "L", PoIssuedByBuyerWorkbook.AllBuyers)[PoIssuedByBuyerWorkbook.Total].Should().Be(1);
        Row(sheet, PoIssuedByBuyerWorkbook.AllDivisions, PoIssuedByBuyerWorkbook.AllBuyers)[PoIssuedByBuyerWorkbook.Total]
            .Should().Be(5);
    }

    [Fact]
    public void PosSheet_NeverPrintedPo_IsNotCountedAsIssued()
    {
        Row(Build(), "L", "KTLOH")[PoIssuedByBuyerWorkbook.Total].Should().Be(1);
    }

    [Fact]
    public void LinesSheet_CountsPoLines()
    {
        var lines = Build().SubReports.Single(s => s.SectionLabel == PoIssuedByBuyerWorkbook.LinesSheet);

        Row(lines, "D", "BKOOI")["w/c 2026-10-05"].Should().Be(4);
        Row(lines, "D", "BKOOI")[PoIssuedByBuyerWorkbook.Total].Should().Be(6);
    }

    [Fact]
    public void DailySheet_ShowsIssuedAndCreatedSeparately()
    {
        var daily = Build().SubReports.Single(s => s.SectionLabel == PoIssuedByBuyerWorkbook.DailySheet);

        var ktloh15 = daily.Rows.Single(r => (DateTime)r["Date"]! == new DateTime(2026, 10, 15) && (string)r["Buyer"]! == "KTLOH");
        ktloh15["POs issued"].Should().Be(0);
        ktloh15["POs created"].Should().Be(1);

        // Issued before the period but created inside it: counted as created only.
        var bkooi3 = daily.Rows.Single(r => (DateTime)r["Date"]! == new DateTime(2026, 10, 3));
        bkooi3["POs issued"].Should().Be(0);
        bkooi3["POs created"].Should().Be(1);
    }

    [Fact]
    public void DetailSheet_ListsEveryPo_AndSaysWhichAreCounted()
    {
        var detail = Build().SubReports.Single(s => s.SectionLabel == PoIssuedByBuyerWorkbook.DetailSheet);

        detail.Rows.Should().HaveCount(Orders.Length);
        detail.Rows.Count(r => (string)r["Issued in period"]! == "Yes").Should().Be(5);
    }

    [Fact]
    public void BlankBuyer_IsShownAsNoBuyer()
    {
        var sheet = PoIssuedByBuyerWorkbook.Build(PoIssuedByBuyerFetcher.Id, "POs Issued by Buyer",
            [Po("D", " ", "2026-10-06")], Run, DateTime.UtcNow);

        sheet.Rows.Should().Contain(r => (string)r["Buyer"]! == PoIssuedByBuyerWorkbook.NoBuyer);
    }

    [Fact]
    public async Task Workbook_RendersToExcel_WithOneSheetPerSection()
    {
        var report = new Reporting.Core.Catalog.ReportDefinition
        {
            Id = PoIssuedByBuyerFetcher.Id, DisplayName = "POs Issued by Buyer", Domain = "SupplyChain",
            DataSource = "Db2Direct"
        };

        var output = await new Reporting.Infrastructure.Renderers.ExcelRenderer().RenderAsync(Build(), report);

        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(output.Content));
        workbook.Worksheets.Select(w => w.Name).Should().Equal(
            "POs Issued by Buyer", PoIssuedByBuyerWorkbook.LinesSheet, PoIssuedByBuyerWorkbook.DailySheet,
            PoIssuedByBuyerWorkbook.DetailSheet, PoIssuedByBuyerWorkbook.DefinitionsSheet);
    }

    [Fact]
    public void NoPos_GivesEmptyCountSheets_NotAnError()
    {
        var sheet = PoIssuedByBuyerWorkbook.Build(PoIssuedByBuyerFetcher.Id, "POs Issued by Buyer", [], Run, DateTime.UtcNow);

        sheet.Rows.Should().BeEmpty();
        sheet.SubReports.Should().HaveCount(4);
    }
}
