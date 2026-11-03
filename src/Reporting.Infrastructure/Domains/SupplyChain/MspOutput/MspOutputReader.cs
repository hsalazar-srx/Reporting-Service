using System.Globalization;
using ClosedXML.Excel;

namespace Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

/// <summary>A file the reader can't use: a required column is missing, or rows are out of order.</summary>
public sealed class MspOutputFormatException(string message) : Exception(message);

/// <summary>What the reader found in one file: the columns present and the rows.</summary>
public sealed record MspOutputFile(IReadOnlyList<string> Columns, IReadOnlyList<MspOutputLine> Lines);

/// <summary>
/// Reads an MSP report workbook written by the legacy PHP MSP (first worksheet, header row 1).
///
/// Columns are found by their header text, never by position: the layout grew from 18 columns
/// (A–R) to 20 with hotfix 1 (14 Oct 2026) and to 23 with hotfix 2 (21 Oct 2026), and the
/// on-screen "with comments" variant adds Comment, Remarks and Tracking after them. Columns the
/// reader doesn't know are ignored; a missing required column is an error.
///
/// Values as MSP writes them: dates are dd/MM/yyyy text, number-like cells (Order, quantities,
/// sometimes Item) are numbers, and text is space-padded (Supplier has a leading space). The
/// reader trims text and treats blank or all-space cells as null.
///
/// Read-only: the file is opened for reading and never changed.
/// </summary>
public static class MspOutputReader
{
    public const string MoStartDate = "MO Start Date";
    public const string Item = "Item";
    public const string Order = "Order";
    public const string Status = "Status";
    public const string Delay = "Delay";
    public const string ReqQty = "Req Qty";
    public const string QtyShort = "Qty Short";
    public const string RefOrder = "Ref Order";
    public const string PlannedDate = "Planned Date";
    public const string EnteredDate = "Entered Date";
    public const string MatDue = "Mat Due";
    public const string Supplier = "Supplier";
    public const string Description = "Description";
    public const string Buyer = "Buyer";
    public const string Customer = "Customer";
    public const string BuyStatus = "Buy Status";
    public const string Type = "Type";
    public const string CustomerSupplied = "Customer Supplied";
    public const string LeadTime = "Lead Time (days)";
    public const string PoLine = "PO Line";
    public const string MpnPrime = "MPN (Prime)";
    public const string MfrPrime = "MFR (Prime)";
    public const string MpnAlternative = "MPN (Alternative)";

    /// <summary>Columns A–R, present in every MSP file since 2017.</summary>
    public static readonly IReadOnlyList<string> RequiredColumns =
    [
        MoStartDate, Item, Order, Status, Delay, ReqQty, QtyShort, RefOrder, PlannedDate, EnteredDate,
        MatDue, Supplier, Description, Buyer, Customer, BuyStatus, Type, CustomerSupplied,
    ];

    /// <summary>Columns S–W, added by the hotfixes; null on rows of older files.</summary>
    public static readonly IReadOnlyList<string> OptionalColumns =
        [LeadTime, PoLine, MpnPrime, MfrPrime, MpnAlternative];

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy"];

    /// <summary>
    /// How MSP writes an empty MOVEX date (0): seen on "At QC inspection" lines, whose PO has no
    /// planned date once received (3 Nov 2026 D100 file, Planned Date, 3 rows).
    /// </summary>
    private const string EmptyMovexDate = "//0";

    public static MspOutputFile Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Read(stream);
    }

    public static MspOutputFile Read(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);

        var headerRow = sheet.Row(1);
        var columnOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var present = new List<string>();
        foreach (var cell in headerRow.CellsUsed())
        {
            var name = Normalise(cell.GetString());
            if (name.Length == 0 || columnOf.ContainsKey(name))
                continue;
            columnOf[name] = cell.Address.ColumnNumber;
            present.Add(name);
        }

        var missing = RequiredColumns.Where(c => !columnOf.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new MspOutputFormatException(
                $"Not an MSP report: missing column(s) {string.Join(", ", missing)} in the header row.");

        var lines = new List<MspOutputLine>();
        string? currentMo = null;
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (var r = 2; r <= lastRow; r++)
        {
            var row = sheet.Row(r);
            string? Text(string column) =>
                columnOf.TryGetValue(column, out var c) ? ReadText(row.Cell(c)) : null;

            var item = Text(Item);
            var buyStatus = Text(BuyStatus);
            if (item is null && buyStatus is null && Text(Order) is null)
                continue; // blank row

            if (item is null)
                throw new MspOutputFormatException($"Row {r}: Item is blank.");

            var isHeader = buyStatus is null;
            if (isHeader)
                currentMo = Text(Order)
                    ?? throw new MspOutputFormatException($"Row {r}: MO header row has no Order.");
            else if (currentMo is null)
                throw new MspOutputFormatException($"Row {r}: component row before any MO header row.");

            DateOnly? Date(string column) =>
                columnOf.TryGetValue(column, out var c) ? ReadDate(row.Cell(c), r, column) : null;
            decimal? Number(string column) =>
                columnOf.TryGetValue(column, out var c) ? ReadNumber(row.Cell(c), r, column) : null;

            lines.Add(new MspOutputLine
            {
                RowNumber = r,
                IsMoHeader = isHeader,
                MoOrder = currentMo!,
                Occurrence = 0, // numbered below, once the whole MO has been read
                MoStartDate = Date(MoStartDate),
                Item = item,
                Order = Text(Order),
                Status = Text(Status),
                Delay = Text(Delay),
                ReqQty = Number(ReqQty),
                QtyShort = Number(QtyShort),
                RefOrder = Text(RefOrder),
                PlannedDate = Date(PlannedDate),
                EnteredDate = Date(EnteredDate),
                MatDue = Date(MatDue),
                Supplier = Text(Supplier),
                Description = Text(Description),
                Buyer = Text(Buyer),
                Customer = Text(Customer),
                BuyStatus = buyStatus,
                Type = Text(Type),
                CustomerSupplied = Text(CustomerSupplied),
                LeadTimeDays = Number(LeadTime) is { } days ? (int)days : null,
                PoLine = Text(PoLine),
                MpnPrime = Text(MpnPrime),
                MfrPrime = Text(MfrPrime),
                MpnAlternative = Text(MpnAlternative),
            });
        }

        return new MspOutputFile(present, NumberOccurrences(lines));
    }

    /// <summary>
    /// Numbers component rows that share an MO and item (an item used twice in one MO, or one
    /// need covered by two POs) by Req Qty, then Qty Short, then PO line, then row, so the
    /// numbering doesn't depend on the order MSP happens to list them in. Buyer entries are keyed
    /// on it, so it must be the same every day.
    /// </summary>
    private static List<MspOutputLine> NumberOccurrences(List<MspOutputLine> lines)
    {
        var occurrenceOf = lines
            .Where(l => !l.IsMoHeader)
            .GroupBy(l => (l.MoOrder, l.Item))
            .SelectMany(g => g
                .OrderBy(l => l.ReqQty)
                .ThenBy(l => l.QtyShort)
                .ThenBy(l => l.PoLine, StringComparer.Ordinal)
                .ThenBy(l => l.RowNumber)
                .Select((l, i) => (l.RowNumber, Occurrence: i + 1)))
            .ToDictionary(x => x.RowNumber, x => x.Occurrence);

        return lines
            .Select(l => l.IsMoHeader ? l : l with { Occurrence = occurrenceOf[l.RowNumber] })
            .ToList();
    }

    /// <summary>Header text compared without case and with runs of spaces collapsed.</summary>
    private static string Normalise(string header) =>
        string.Join(' ', header.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? ReadText(IXLCell cell)
    {
        var value = cell.Value;
        var text = value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Number => value.GetNumber().ToString(CultureInfo.InvariantCulture),
            XLDataType.DateTime => value.GetDateTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            _ => cell.GetString(),
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static DateOnly? ReadDate(IXLCell cell, int row, string column)
    {
        var value = cell.Value;
        if (value.Type == XLDataType.DateTime)
            return DateOnly.FromDateTime(value.GetDateTime());

        var text = ReadText(cell);
        if (text is null or EmptyMovexDate)
            return null;
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw new MspOutputFormatException($"Row {row}: {column} '{text}' is not a dd/MM/yyyy date.");
    }

    private static decimal? ReadNumber(IXLCell cell, int row, string column)
    {
        var value = cell.Value;
        if (value.Type == XLDataType.Number)
            return (decimal)value.GetNumber();

        var text = ReadText(cell);
        if (text is null)
            return null;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            return number;
        throw new MspOutputFormatException($"Row {row}: {column} '{text}' is not a number.");
    }
}
