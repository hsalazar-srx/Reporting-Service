namespace Reporting.Core.Catalog;

/// <summary>
/// In-app help shown beside a report (SM-Portal's "How to read this report" panel).
/// Declared per report in config/report-catalog.json, so adding or changing help needs no portal change.
/// Deliberately plain — titles, text, term/meaning pairs and bullets, no markup — so any client can render it.
/// </summary>
public sealed record ReportHelp
{
    public IReadOnlyList<ReportHelpSection> Sections { get; init; } = [];
}

/// <summary>One titled block of help. Any of Text, Items and Bullets may be present, shown in that order.</summary>
public sealed record ReportHelpSection
{
    public required string Title { get; init; }

    /// <summary>An opening paragraph.</summary>
    public string? Text { get; init; }

    /// <summary>Term and meaning pairs — a glossary, or the sheets of a workbook.</summary>
    public IReadOnlyList<ReportHelpItem> Items { get; init; } = [];

    /// <summary>Short parallel points.</summary>
    public IReadOnlyList<string> Bullets { get; init; } = [];
}

public sealed record ReportHelpItem
{
    public required string Term { get; init; }
    public required string Text { get; init; }
}
