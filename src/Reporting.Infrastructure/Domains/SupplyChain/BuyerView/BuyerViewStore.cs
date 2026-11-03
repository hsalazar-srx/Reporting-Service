using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

namespace Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

/// <summary>What one sync did to the store.</summary>
public sealed record SyncRunResult(
    string Division,
    string FileName,
    string Result,
    string? Message,
    int RowsRead = 0,
    int LinesNew = 0,
    int LinesUpdated = 0,
    int LinesClosed = 0,
    int LinesReopened = 0)
{
    public const string Applied = "Applied";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

/// <summary>
/// The buyer view's own SQLite file: the lines MSP reports (refreshed by each sync), the buyers'
/// entries against them, a log of every buyer edit, and a record of each sync.
///
/// This is the only thing Reporting-Service writes; its sources (MOVEX, DBSRV, the MSP output
/// folder on SRXAPP03) stay read-only. Only Reporting-Service opens the file.
///
/// Lines are keyed by division + <see cref="MspOutputLine.Key"/> (MOVEX identifiers), so buyer
/// entries survive every sync and can move to Oskar unchanged. Dates are stored as yyyy-MM-dd text.
/// </summary>
public sealed class BuyerViewStore(string databasePath)
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false, // release the file between operations so backups and tests can copy or delete it
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 5000;");
        return connection;
    }

    /// <summary>Creates the tables if they don't exist and switches the file to WAL mode. Safe to call every start.</summary>
    public void EnsureSchema()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS SchemaInfo (Version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS SyncRun (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Division      TEXT NOT NULL,
                FileName      TEXT NOT NULL,
                FileWrittenAt TEXT NOT NULL,
                RunDate       TEXT NOT NULL,
                StartedUtc    TEXT NOT NULL,
                FinishedUtc   TEXT NOT NULL,
                Result        TEXT NOT NULL,
                Message       TEXT,
                RowsRead      INTEGER NOT NULL,
                LinesNew      INTEGER NOT NULL,
                LinesUpdated  INTEGER NOT NULL,
                LinesClosed   INTEGER NOT NULL,
                LinesReopened INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS Line (
                Division         TEXT NOT NULL,
                LineKey          TEXT NOT NULL,
                IsMoHeader       INTEGER NOT NULL,
                MoOrder          TEXT NOT NULL,
                Occurrence       INTEGER NOT NULL,
                MoStartDate      TEXT,
                Item             TEXT NOT NULL,
                OrderNo          TEXT,
                Status           TEXT,
                ReqQty           REAL,
                QtyShort         REAL,
                RefOrder         TEXT,
                PlannedDate      TEXT,
                EnteredDate      TEXT,
                MatDue           TEXT,
                Supplier         TEXT,
                Description      TEXT,
                Buyer            TEXT,
                Customer         TEXT,
                BuyStatus        TEXT,
                Type             TEXT,
                CustomerSupplied TEXT,
                LeadTimeDays     INTEGER,
                PoLine           TEXT,
                MpnPrime         TEXT,
                MfrPrime         TEXT,
                MpnAlternative   TEXT,
                FirstSeenDate    TEXT NOT NULL,
                LastSeenDate     TEXT NOT NULL,
                LastSyncRunId    INTEGER NOT NULL,
                ClosedDate       TEXT,
                PRIMARY KEY (Division, LineKey));

            CREATE INDEX IF NOT EXISTS IX_Line_Buyer ON Line (Division, Buyer, ClosedDate);

            CREATE TABLE IF NOT EXISTS LineEntry (
                Division      TEXT NOT NULL,
                LineKey       TEXT NOT NULL,
                Category      TEXT,
                Details       TEXT,
                Eta           TEXT,
                Tracking      TEXT,
                PlannersNotes TEXT,
                UpdatedBy     TEXT NOT NULL,
                UpdatedUtc    TEXT NOT NULL,
                PRIMARY KEY (Division, LineKey));

            CREATE TABLE IF NOT EXISTS EntryChange (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                Division   TEXT NOT NULL,
                LineKey    TEXT NOT NULL,
                Field      TEXT NOT NULL,
                OldValue   TEXT,
                NewValue   TEXT,
                ChangedBy  TEXT NOT NULL,
                ChangedUtc TEXT NOT NULL);

            CREATE INDEX IF NOT EXISTS IX_EntryChange_Line ON EntryChange (Division, LineKey);
            """);

        var version = connection.ExecuteScalar<int?>("SELECT MAX(Version) FROM SchemaInfo;");
        if (version is null)
            connection.Execute("INSERT INTO SchemaInfo (Version) VALUES (@SchemaVersion);", new { SchemaVersion });
        else if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"Buyer view database {databasePath} is schema version {version}; this build knows only up to {SchemaVersion}.");
    }

    /// <summary>True when this exact file (name and write time) has already been applied.</summary>
    public bool WasApplied(string fileName, DateTime fileWrittenAt)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM SyncRun WHERE FileName = @fileName AND FileWrittenAt = @written AND Result = 'Applied';",
            new { fileName, written = Stamp(fileWrittenAt) }) > 0;
    }

    /// <summary>Rows read by the division's last applied sync, or null if it has never synced.</summary>
    public int? LastAppliedRowCount(string division)
    {
        using var connection = Open();
        return connection.ExecuteScalar<int?>(
            "SELECT RowsRead FROM SyncRun WHERE Division = @division AND Result = 'Applied' ORDER BY Id DESC LIMIT 1;",
            new { division });
    }

    /// <summary>Records a sync that didn't change any line (skipped or failed).</summary>
    public void RecordRun(SyncRunResult result, DateTime fileWrittenAt, DateOnly runDate, DateTime startedUtc, DateTime finishedUtc)
    {
        using var connection = Open();
        InsertRun(connection, null, result, fileWrittenAt, runDate, startedUtc, finishedUtc);
    }

    /// <summary>
    /// Applies one day's MSP file to the division's lines, in one transaction:
    /// new keys are added, existing ones refreshed (and reopened if they had closed), and open
    /// lines missing from the file are closed on <paramref name="runDate"/>. Buyer entries are
    /// never touched.
    /// </summary>
    public SyncRunResult ApplySnapshot(
        string division, string fileName, DateTime fileWrittenAt, DateOnly runDate,
        IReadOnlyList<MspOutputLine> lines, DateTime startedUtc, DateTime finishedUtc)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        var existing = connection
            .Query<(string LineKey, string? ClosedDate)>(
                "SELECT LineKey, ClosedDate FROM Line WHERE Division = @division;", new { division }, transaction)
            .ToDictionary(x => x.LineKey, x => x.ClosedDate);

        var inFile = lines.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var linesNew = lines.Count(l => !existing.ContainsKey(l.Key));
        var linesReopened = lines.Count(l => existing.TryGetValue(l.Key, out var closed) && closed is not null);
        var linesUpdated = lines.Count - linesNew;
        var toClose = existing.Where(e => e.Value is null && !inFile.Contains(e.Key)).Select(e => e.Key).ToList();

        var result = new SyncRunResult(division, fileName, SyncRunResult.Applied, null,
            lines.Count, linesNew, linesUpdated, toClose.Count, linesReopened);
        var runId = InsertRun(connection, transaction, result, fileWrittenAt, runDate, startedUtc, finishedUtc);
        var day = Day(runDate);

        connection.Execute("""
            INSERT INTO Line (Division, LineKey, IsMoHeader, MoOrder, Occurrence, MoStartDate, Item, OrderNo, Status,
                ReqQty, QtyShort, RefOrder, PlannedDate, EnteredDate, MatDue, Supplier, Description, Buyer, Customer,
                BuyStatus, Type, CustomerSupplied, LeadTimeDays, PoLine, MpnPrime, MfrPrime, MpnAlternative,
                FirstSeenDate, LastSeenDate, LastSyncRunId, ClosedDate)
            VALUES (@Division, @LineKey, @IsMoHeader, @MoOrder, @Occurrence, @MoStartDate, @Item, @OrderNo, @Status,
                @ReqQty, @QtyShort, @RefOrder, @PlannedDate, @EnteredDate, @MatDue, @Supplier, @Description, @Buyer, @Customer,
                @BuyStatus, @Type, @CustomerSupplied, @LeadTimeDays, @PoLine, @MpnPrime, @MfrPrime, @MpnAlternative,
                @Day, @Day, @RunId, NULL)
            ON CONFLICT (Division, LineKey) DO UPDATE SET
                IsMoHeader = excluded.IsMoHeader, MoOrder = excluded.MoOrder, Occurrence = excluded.Occurrence,
                MoStartDate = excluded.MoStartDate, Item = excluded.Item, OrderNo = excluded.OrderNo,
                Status = excluded.Status, ReqQty = excluded.ReqQty, QtyShort = excluded.QtyShort,
                RefOrder = excluded.RefOrder, PlannedDate = excluded.PlannedDate, EnteredDate = excluded.EnteredDate,
                MatDue = excluded.MatDue, Supplier = excluded.Supplier, Description = excluded.Description,
                Buyer = excluded.Buyer, Customer = excluded.Customer, BuyStatus = excluded.BuyStatus,
                Type = excluded.Type, CustomerSupplied = excluded.CustomerSupplied,
                LeadTimeDays = excluded.LeadTimeDays, PoLine = excluded.PoLine, MpnPrime = excluded.MpnPrime,
                MfrPrime = excluded.MfrPrime, MpnAlternative = excluded.MpnAlternative,
                LastSeenDate = excluded.LastSeenDate, LastSyncRunId = excluded.LastSyncRunId, ClosedDate = NULL;
            """,
            lines.Select(l => new
            {
                Division = division, LineKey = l.Key, IsMoHeader = l.IsMoHeader ? 1 : 0, l.MoOrder, l.Occurrence,
                MoStartDate = Day(l.MoStartDate), l.Item, OrderNo = l.Order, l.Status,
                ReqQty = (double?)l.ReqQty, QtyShort = (double?)l.QtyShort, l.RefOrder,
                PlannedDate = Day(l.PlannedDate), EnteredDate = Day(l.EnteredDate), MatDue = Day(l.MatDue),
                l.Supplier, l.Description, l.Buyer, l.Customer, l.BuyStatus, l.Type, l.CustomerSupplied,
                l.LeadTimeDays, l.PoLine, l.MpnPrime, l.MfrPrime, l.MpnAlternative, Day = day, RunId = runId,
            }),
            transaction);

        connection.Execute(
            "UPDATE Line SET ClosedDate = @Day WHERE Division = @Division AND LineKey = @LineKey;",
            toClose.Select(key => new { Day = day, Division = division, LineKey = key }),
            transaction);

        transaction.Commit();
        return result;
    }

    private static long InsertRun(SqliteConnection connection, SqliteTransaction? transaction, SyncRunResult result,
        DateTime fileWrittenAt, DateOnly runDate, DateTime startedUtc, DateTime finishedUtc) =>
        connection.ExecuteScalar<long>("""
            INSERT INTO SyncRun (Division, FileName, FileWrittenAt, RunDate, StartedUtc, FinishedUtc, Result, Message,
                RowsRead, LinesNew, LinesUpdated, LinesClosed, LinesReopened)
            VALUES (@Division, @FileName, @FileWrittenAt, @RunDate, @StartedUtc, @FinishedUtc, @Result, @Message,
                @RowsRead, @LinesNew, @LinesUpdated, @LinesClosed, @LinesReopened);
            SELECT last_insert_rowid();
            """,
            new
            {
                result.Division, result.FileName, FileWrittenAt = Stamp(fileWrittenAt), RunDate = Day(runDate),
                StartedUtc = Stamp(startedUtc), FinishedUtc = Stamp(finishedUtc), result.Result, result.Message,
                result.RowsRead, result.LinesNew, result.LinesUpdated, result.LinesClosed, result.LinesReopened,
            },
            transaction);

    private static string? Day(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
