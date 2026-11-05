namespace Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

/// <summary>
/// One row of an MSP report file. MSP writes an MO header row (blank Buy Status) followed by
/// that MO's component rows; component rows don't repeat the MO number, so the reader copies
/// it from the header above (<see cref="MoOrder"/>).
///
/// Columns S–W were added by the PHP hotfixes (Lead Time and PO Line on 14 Oct 2026; MPN and
/// MFR on 21 Oct 2026), so they are null when the file predates them.
/// </summary>
public sealed record MspOutputLine
{
    /// <summary>Row number in the worksheet (1 = the header row), for error messages and tracing.</summary>
    public required int RowNumber { get; init; }

    /// <summary>True for an MO header row; false for a component row.</summary>
    public required bool IsMoHeader { get; init; }

    /// <summary>The MO the row belongs to: its own Order on a header row, the header's Order on a component row.</summary>
    public required string MoOrder { get; init; }

    /// <summary>
    /// 1 for the first component row with this MO and item, 2 for the second, and so on; 0 on a
    /// header row. An MO and item repeat when the item is used twice in the MO, or when MSP
    /// covers its need from more than one PO. Counted in order of Req Qty, Qty Short, PO line.
    /// </summary>
    public required int Occurrence { get; init; }

    public DateOnly? MoStartDate { get; init; }
    public required string Item { get; init; }
    public string? Order { get; init; }
    public string? Status { get; init; }
    public string? Delay { get; init; }
    public decimal? ReqQty { get; init; }
    public decimal? QtyShort { get; init; }
    public string? RefOrder { get; init; }
    public DateOnly? PlannedDate { get; init; }
    public DateOnly? EnteredDate { get; init; }
    public DateOnly? MatDue { get; init; }
    public string? Supplier { get; init; }
    public string? Description { get; init; }
    public string? Buyer { get; init; }
    public string? Customer { get; init; }
    public string? BuyStatus { get; init; }
    public string? Type { get; init; }
    public string? CustomerSupplied { get; init; }
    public int? LeadTimeDays { get; init; }
    public string? PoLine { get; init; }
    public string? MpnPrime { get; init; }
    public string? MfrPrime { get; init; }
    public string? MpnAlternative { get; init; }

    /// <summary>
    /// Identifies the line from one day's file to the next: the MO for a header row; MO, item
    /// and occurrence for a component row (this MO's need for this item). The PO line is not
    /// part of it, so when a buyer raises a PO, or MSP moves the need to another PO, the line
    /// and its buyer entries stay the same (Hector, 2026-11-03). Built from MOVEX identifiers so
    /// buyer entries keyed on it can move to Oskar unchanged.
    /// </summary>
    public string Key => IsMoHeader
        ? $"MO|{MoOrder}"
        : $"{MoOrder}|{Item}|{Occurrence}";
}
