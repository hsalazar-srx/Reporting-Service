using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Reporting.Infrastructure.Domains.SupplyChain.BuyerView;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class BuyerViewStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"buyerview-{Guid.NewGuid():N}.db");
    private readonly BuyerViewStore _store;

    private static readonly DateTime Started = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);

    public BuyerViewStoreTests()
    {
        _store = new BuyerViewStore(_path);
        _store.EnsureSchema();
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file))
                File.Delete(file);
    }

    internal static MspOutputLine Header(string mo) => new()
    {
        RowNumber = 2, IsMoHeader = true, MoOrder = mo, Occurrence = 0, Item = "FG-" + mo, Order = mo,
        MoStartDate = new DateOnly(2026, 11, 30), Customer = "LAB3 PTY LTD",
    };

    internal static MspOutputLine Component(string mo, string item, string? poLine = null, string buyStatus = "No purchase order") => new()
    {
        RowNumber = 3, IsMoHeader = false, MoOrder = mo, Occurrence = 1, Item = item, PoLine = poLine,
        Order = poLine?.Split('.')[0], BuyStatus = buyStatus, Buyer = "YPKHOR10", Supplier = "TBA",
        ReqQty = 590m, LeadTimeDays = 53, MoStartDate = new DateOnly(2026, 11, 30),
        PlannedDate = new DateOnly(2027, 1, 15),
    };

    private SyncRunResult Apply(DateOnly day, params MspOutputLine[] lines) =>
        _store.ApplySnapshot("D", $"MSP-{day:yyyyMMdd}.xlsx", day.ToDateTime(new TimeOnly(5, 50)), day, lines,
            Started, Started.AddMinutes(1));

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static readonly DateOnly Mon = new(2026, 11, 2);
    private static readonly DateOnly Tue = new(2026, 11, 3);
    private static readonly DateOnly Wed = new(2026, 11, 4);

    [Fact]
    public void EnsureSchema_CalledAgain_KeepsDataAndUsesWal()
    {
        Apply(Mon, Header("9118010"));

        _store.EnsureSchema();

        using var connection = Connect();
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM Line;").Should().Be(1);
        connection.ExecuteScalar<string>("PRAGMA journal_mode;").Should().Be("wal");
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM SchemaInfo;").Should().Be(1);
    }

    [Fact]
    public void ApplySnapshot_FirstFile_AddsEveryLine()
    {
        var result = Apply(Mon, Header("9118010"), Component("9118010", "LFLB050001FG"));

        result.Result.Should().Be(SyncRunResult.Applied);
        result.LinesNew.Should().Be(2);
        result.LinesUpdated.Should().Be(0);
        result.LinesClosed.Should().Be(0);
    }

    [Fact]
    public void ApplySnapshot_StoresTheMspColumns()
    {
        Apply(Mon, Header("9118010"), Component("9118010", "LFLB700002", "5305842.1.0", "OK"));

        using var connection = Connect();
        var line = connection.QuerySingle(
            "SELECT * FROM Line WHERE LineKey = '9118010|LFLB700002|1';");
        ((string)line.BuyStatus).Should().Be("OK");
        ((string)line.OrderNo).Should().Be("5305842");
        ((string)line.PlannedDate).Should().Be("2027-01-15");
        ((double)line.ReqQty).Should().Be(590);
        ((long)line.LeadTimeDays).Should().Be(53);
        ((string)line.FirstSeenDate).Should().Be("2026-11-02");
    }

    [Fact]
    public void ApplySnapshot_LineGoneFromTheNextFile_IsClosedNotDeleted()
    {
        Apply(Mon, Header("9118010"), Component("9118010", "A"), Component("9118010", "B"));

        var result = Apply(Tue, Header("9118010"), Component("9118010", "A"));

        result.LinesClosed.Should().Be(1);
        using var connection = Connect();
        connection.ExecuteScalar<string>("SELECT ClosedDate FROM Line WHERE LineKey = '9118010|B|1';")
            .Should().Be("2026-11-03");
        connection.ExecuteScalar<string?>("SELECT ClosedDate FROM Line WHERE LineKey = '9118010|A|1';")
            .Should().BeNull();
    }

    [Fact]
    public void ApplySnapshot_ClosedLineComesBack_IsReopened()
    {
        Apply(Mon, Header("9118010"), Component("9118010", "A"));
        Apply(Tue, Header("9118010"));

        var result = Apply(Wed, Header("9118010"), Component("9118010", "A"));

        result.LinesReopened.Should().Be(1);
        result.LinesNew.Should().Be(0);
        using var connection = Connect();
        connection.ExecuteScalar<string?>("SELECT ClosedDate FROM Line WHERE LineKey = '9118010|A|1';").Should().BeNull();
        connection.ExecuteScalar<string>("SELECT FirstSeenDate FROM Line WHERE LineKey = '9118010|A|1';").Should().Be("2026-11-02");
    }

    [Fact]
    public void ApplySnapshot_PoRaisedForANoPoLine_UpdatesTheSameLine()
    {
        // The PO line is not part of the key, so raising the PO updates the line and its
        // buyer entries stay with it.
        Apply(Mon, Header("9118010"), Component("9118010", "A"));

        var result = Apply(Tue, Header("9118010"), Component("9118010", "A", "5305842.1.0", "Unconfirmed PO"));

        result.LinesNew.Should().Be(0);
        result.LinesClosed.Should().Be(0);
        using var connection = Connect();
        connection.QuerySingle<(string PoLine, string BuyStatus)>(
                "SELECT PoLine, BuyStatus FROM Line WHERE LineKey = '9118010|A|1';")
            .Should().Be(("5305842.1.0", "Unconfirmed PO"));
    }

    [Fact]
    public void ApplySnapshot_NeverTouchesBuyerEntries()
    {
        Apply(Mon, Header("9118010"), Component("9118010", "A"));
        using (var connection = Connect())
            connection.Execute("""
                INSERT INTO LineEntry (Division, LineKey, Category, Details, UpdatedBy, UpdatedUtc)
                VALUES ('D', '9118010|A|1', 'Expediting', 'DHL 5112674965', 'SRXGLOBAL\buyer', '2026-11-02T09:00:00');
                """);

        Apply(Tue, Header("9118010"));                                  // closes the line
        Apply(Wed, Header("9118010"), Component("9118010", "A"));       // reopens it

        using var check = Connect();
        check.QuerySingle<(string Category, string Details)>(
                "SELECT Category, Details FROM LineEntry WHERE LineKey = '9118010|A|1';")
            .Should().Be(("Expediting", "DHL 5112674965"));
    }

    [Fact]
    public void ApplySnapshot_OtherDivisionsLines_AreLeftAlone()
    {
        _store.ApplySnapshot("L", "MSP-L.xlsx", Mon.ToDateTime(TimeOnly.MinValue), Mon,
            [Header("9200001"), Component("9200001", "X")], Started, Started);

        var result = Apply(Tue, Header("9118010"));

        result.LinesClosed.Should().Be(0);
    }

    [Fact]
    public void WasApplied_AndLastAppliedRowCount_FollowTheRuns()
    {
        var written = Mon.ToDateTime(new TimeOnly(5, 50));
        _store.WasApplied("MSP-20261102.xlsx", written).Should().BeFalse();
        _store.LastAppliedRowCount("D").Should().BeNull();

        Apply(Mon, Header("9118010"), Component("9118010", "A"));

        _store.WasApplied("MSP-20261102.xlsx", written).Should().BeTrue();
        _store.WasApplied("MSP-20261102.xlsx", written.AddMinutes(1)).Should().BeFalse(); // re-written file
        _store.LastAppliedRowCount("D").Should().Be(2);
    }
}
