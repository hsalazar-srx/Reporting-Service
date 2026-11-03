using ClosedXML.Excel;
using FluentAssertions;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class MspOutputReaderTests
{
    private static readonly string[] LayoutAtoR =
    [
        "MO Start Date", "Item", "Order", "Status", "Delay", "Req Qty", "Qty Short", "Ref Order", "Planned Date",
        "Entered Date", "Mat Due", "Supplier", "Description", "Buyer", "Customer", "Buy Status", "Type",
        "Customer Supplied",
    ];

    private static readonly string[] LayoutAtoW =
        [.. LayoutAtoR, "Lead Time (days)", "PO Line", "MPN (Prime)", "MFR (Prime)", "MPN (Alternative)"];

    // Rows as the PHP MSP writes them: dd/MM/yyyy text dates, numbers for Order and quantities,
    // space-padded text, blank Buy Status on the MO header row.
    private static readonly Dictionary<string, object?> MoHeader = new()
    {
        ["MO Start Date"] = "07/04/2025", ["Item"] = "LFLB050002", ["Order"] = 9118010d, ["Req Qty"] = 590d,
        ["Ref Order"] = 7062307d, ["Planned Date"] = "07/04/2025", ["Entered Date"] = "07/03/2025",
        ["Mat Due"] = "07/04/2025", ["Description"] = "SensorMine Nexus Final Assy   ", ["Customer"] = "LAB3 PTY LTD",
    };

    private static readonly Dictionary<string, object?> NoPoComponent = new()
    {
        ["MO Start Date"] = "07/04/2025", ["Item"] = "LFLB050001FG   ", ["Req Qty"] = 590d, ["Qty Short"] = 590d,
        ["Planned Date"] = "15/01/2027", ["Supplier"] = " TBA       ", ["Description"] = "SensorMine Nexus PCBA FG      ",
        ["Buyer"] = "YPKHOR10  ", ["Customer"] = "LAB3 PTY LTD", ["Buy Status"] = "No purchase order",
        ["Type"] = "No SMT", ["Lead Time (days)"] = 53d,
    };

    private static readonly Dictionary<string, object?> PoComponent = new()
    {
        ["MO Start Date"] = "07/04/2025", ["Item"] = "LFLB700002     ", ["Order"] = "5305842", ["Status"] = "35",
        ["Req Qty"] = 1180d, ["Planned Date"] = "20/11/2026", ["Supplier"] = " DIGI-KEY C",
        ["Description"] = "LABEL 50 X 32mm RT5032-2.5MP  ", ["Buyer"] = "YPKHOR10  ", ["Customer"] = "LAB3 PTY LTD",
        ["Buy Status"] = "OK", ["Type"] = "SMT", ["Lead Time (days)"] = 53d, ["PO Line"] = "5305842.1.0",
        ["MPN (Prime)"] = "RT5032-2.5MP", ["MFR (Prime)"] = "BARCODES GROUP PTY LTD",
        ["MPN (Alternative)"] = "BARCODES GROUP PTY LTD (RT5032-2.5MP)",
    };

    private static MemoryStream Workbook(string[] headers, params Dictionary<string, object?>[] rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Worksheet");
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < headers.Length; c++)
                if (rows[r].TryGetValue(headers[c], out var value) && value is not null)
                    sheet.Cell(r + 2, c + 1).Value = value switch
                    {
                        double d => d,
                        DateTime dt => dt,
                        _ => value.ToString(),
                    };
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Read_CurrentLayout_ReadsEveryColumnAndTrimsText()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, NoPoComponent, PoComponent));

        file.Lines.Should().HaveCount(3);
        var po = file.Lines[2];
        po.IsMoHeader.Should().BeFalse();
        po.MoOrder.Should().Be("9118010");
        po.Item.Should().Be("LFLB700002");
        po.Order.Should().Be("5305842");
        po.Status.Should().Be("35");
        po.Supplier.Should().Be("DIGI-KEY C");
        po.Buyer.Should().Be("YPKHOR10");
        po.ReqQty.Should().Be(1180m);
        po.MoStartDate.Should().Be(new DateOnly(2025, 4, 7));
        po.PlannedDate.Should().Be(new DateOnly(2026, 11, 20));
        po.LeadTimeDays.Should().Be(53);
        po.PoLine.Should().Be("5305842.1.0");
        po.MpnPrime.Should().Be("RT5032-2.5MP");
        po.MfrPrime.Should().Be("BARCODES GROUP PTY LTD");
        po.MpnAlternative.Should().Be("BARCODES GROUP PTY LTD (RT5032-2.5MP)");
    }

    [Fact]
    public void Read_MoHeaderRow_HasBlankBuyStatusAndItsOwnOrderAsMo()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, NoPoComponent));

        var header = file.Lines[0];
        header.IsMoHeader.Should().BeTrue();
        header.MoOrder.Should().Be("9118010");
        header.Order.Should().Be("9118010");
        header.RefOrder.Should().Be("7062307");
        header.Occurrence.Should().Be(0);
        header.Key.Should().Be("MO|9118010");
    }

    [Fact]
    public void Read_ComponentRows_TakeTheMoFromTheHeaderAbove()
    {
        var secondHeader = new Dictionary<string, object?>(MoHeader) { ["Order"] = 9118467d };

        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, NoPoComponent, secondHeader, PoComponent));

        file.Lines[1].MoOrder.Should().Be("9118010");
        file.Lines[3].MoOrder.Should().Be("9118467");
    }

    [Fact]
    public void Read_NoPurchaseOrderLine_HasNoPoLine_AndTheKeyNeverIncludesOne()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, NoPoComponent));

        var line = file.Lines[1];
        line.PoLine.Should().BeNull();
        line.Order.Should().BeNull();
        line.Key.Should().Be("9118010|LFLB050001FG|1");
    }

    [Fact]
    public void Read_SameItemTwiceInOneMo_NumbersTheOccurrences()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, NoPoComponent, NoPoComponent));

        file.Lines[1].Occurrence.Should().Be(1);
        file.Lines[2].Occurrence.Should().Be(2);
        file.Lines.Select(l => l.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Read_SameItemTwiceListedInEitherOrder_KeepsTheSameKeys()
    {
        // Buyer entries are keyed on the occurrence, so it follows Req Qty, not row order.
        var small = new Dictionary<string, object?>(NoPoComponent) { ["Req Qty"] = 10d };
        var large = new Dictionary<string, object?>(NoPoComponent) { ["Req Qty"] = 500d };

        var monday = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, small, large));
        var tuesday = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, large, small));

        monday.Lines.Single(l => l.ReqQty == 500m).Key
            .Should().Be(tuesday.Lines.Single(l => l.ReqQty == 500m).Key);
    }

    [Fact]
    public void Read_FileBeforeTheHotfixes_LeavesColumnsSToWNull()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoR, MoHeader, PoComponent));

        var line = file.Lines[1];
        line.LeadTimeDays.Should().BeNull();
        line.PoLine.Should().BeNull();
        line.MpnPrime.Should().BeNull();
        line.MpnAlternative.Should().BeNull();
        file.Columns.Should().NotContain(MspOutputReader.OptionalColumns);
    }

    [Fact]
    public void Read_ColumnsMovedAndCommentColumnsAdded_FindsColumnsByHeader()
    {
        // The on-screen "with comments" variant adds Comment, Remarks and Tracking; a future
        // hotfix may insert columns. Neither may shift what the reader picks up.
        string[] reordered = ["Comment", .. LayoutAtoW.Reverse(), "Remarks", "Tracking"];
        var withComment = new Dictionary<string, object?>(PoComponent) { ["Comment"] = "chase supplier" };

        var file = MspOutputReader.Read(Workbook(reordered, MoHeader, withComment));

        file.Lines[1].PoLine.Should().Be("5305842.1.0");
        file.Lines[1].Supplier.Should().Be("DIGI-KEY C");
        file.Columns.Should().Contain("Comment");
    }

    [Fact]
    public void Read_HeaderSpacingAndCaseDiffer_StillMatches()
    {
        var headers = LayoutAtoW.Select(h => h == "Lead Time (days)" ? "  lead time  (DAYS) " : h).ToArray();
        var row = new Dictionary<string, object?>(PoComponent) { ["  lead time  (DAYS) "] = 53d };

        var file = MspOutputReader.Read(Workbook(headers, MoHeader, row));

        file.Lines[1].LeadTimeDays.Should().Be(53);
    }

    [Fact]
    public void Read_NumericItemNumber_ReadAsTextWithoutDecimals()
    {
        var header = new Dictionary<string, object?>(MoHeader) { ["Item"] = 9909998d };

        var file = MspOutputReader.Read(Workbook(LayoutAtoW, header));

        file.Lines[0].Item.Should().Be("9909998");
    }

    [Fact]
    public void Read_RealExcelDates_AreAcceptedAsWellAsText()
    {
        var header = new Dictionary<string, object?>(MoHeader) { ["MO Start Date"] = new DateTime(2025, 4, 7) };

        var file = MspOutputReader.Read(Workbook(LayoutAtoW, header));

        file.Lines[0].MoStartDate.Should().Be(new DateOnly(2025, 4, 7));
    }

    [Fact]
    public void Read_EmptyMovexDateAsMspWritesIt_IsNull()
    {
        // MSP writes "//0" for a MOVEX date of 0, e.g. the Planned Date of an "At QC inspection" line.
        var received = new Dictionary<string, object?>(PoComponent)
            { ["Buy Status"] = "At QC inspection", ["Status"] = "51", ["Planned Date"] = "//0" };

        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, received));

        file.Lines[1].PlannedDate.Should().BeNull();
    }

    [Fact]
    public void Read_AllSpaceCell_IsNull()
    {
        var header = new Dictionary<string, object?>(MoHeader) { ["Ref Order"] = "          " };

        var file = MspOutputReader.Read(Workbook(LayoutAtoW, header));

        file.Lines[0].RefOrder.Should().BeNull();
    }

    [Fact]
    public void Read_BlankRowsInBetween_AreSkipped()
    {
        var file = MspOutputReader.Read(Workbook(LayoutAtoW, MoHeader, [], NoPoComponent));

        file.Lines.Should().HaveCount(2);
        file.Lines[1].RowNumber.Should().Be(4);
    }

    [Fact]
    public void Read_RequiredColumnMissing_ThrowsNamingIt()
    {
        var headers = LayoutAtoW.Where(h => h != "Buy Status").ToArray();

        var act = () => MspOutputReader.Read(Workbook(headers, MoHeader));

        act.Should().Throw<MspOutputFormatException>().WithMessage("*Buy Status*");
    }

    [Fact]
    public void Read_ComponentBeforeAnyMoHeader_Throws()
    {
        var act = () => MspOutputReader.Read(Workbook(LayoutAtoW, NoPoComponent));

        act.Should().Throw<MspOutputFormatException>().WithMessage("Row 2:*before any MO header*");
    }

    [Fact]
    public void Read_DateNotInMspFormat_ThrowsWithRowAndColumn()
    {
        var header = new Dictionary<string, object?>(MoHeader) { ["Mat Due"] = "2025-04-07" };

        var act = () => MspOutputReader.Read(Workbook(LayoutAtoW, header));

        act.Should().Throw<MspOutputFormatException>().WithMessage("Row 2: Mat Due '2025-04-07'*");
    }
}
