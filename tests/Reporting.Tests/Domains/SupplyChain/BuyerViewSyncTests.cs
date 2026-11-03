using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Reporting.Infrastructure.Domains.SupplyChain.BuyerView;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;
using static Reporting.Tests.Domains.SupplyChain.BuyerViewStoreTests;

namespace Reporting.Tests.Domains.SupplyChain;

public sealed class BuyerViewSyncTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"buyerview-{Guid.NewGuid():N}.db");
    private readonly BuyerViewStore _store;
    private readonly FixedClock _clock = new(new DateTime(2026, 11, 3, 9, 0, 0));
    private readonly List<MspOutputCandidate> _files = [];
    private readonly Dictionary<string, Func<MspOutputFile>> _contents = [];

    private static readonly BuyerViewSyncOptions Options = new() { OutputFolder = @"\\SRXAPP03\MSP-test\Output" };

    public BuyerViewSyncTests() => _store = new BuyerViewStore(_path);

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(file))
                File.Delete(file);
    }

    /// <summary>A clock fixed at a given time, with UTC as its local zone so local and UTC times agree.</summary>
    private sealed class FixedClock(DateTime local) : TimeProvider
    {
        public DateTime Local { get; set; } = local;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Local, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private void AddFile(string name, DateTime writtenAt, params MspOutputLine[] lines)
    {
        _files.Add(new MspOutputCandidate($@"\\SRXAPP03\MSP-test\Output\{name}", writtenAt));
        _contents[$@"\\SRXAPP03\MSP-test\Output\{name}"] = () => new MspOutputFile([], lines);
    }

    private BuyerViewSync Sync() => new(Options, _store, (_, _, _) => _files, path => _contents[path](), _clock,
        NullLogger<BuyerViewSync>.Instance);

    private static MspOutputLine[] Lines(int count) =>
        [Header("9118010"), .. Enumerable.Range(1, count - 1).Select(i => Component("9118010", $"ITEM{i}"))];

    private long Count(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        connection.Open();
        return connection.ExecuteScalar<long>(sql);
    }

    // ── File choice ──────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0);

    private static MspOutputCandidate File_(string name, DateTime written) => new($@"C:\out\{name}", written);

    [Fact]
    public void PickLatestDailyFile_TakesTheNewestDay()
    {
        var picked = BuyerViewSync.PickLatestDailyFile(
            [
                File_("MSP2027050220241102D100.xlsx", new DateTime(2026, 11, 2, 5, 51, 0)),
                File_("MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0)),
            ], "D", "100", Now, TimeSpan.FromMinutes(5));

        picked!.Path.Should().EndWith("MSP2027050320241103D100.xlsx");
    }

    [Fact]
    public void PickLatestDailyFile_IgnoresCustomerRunsOtherDivisionsAndOtherReports()
    {
        var picked = BuyerViewSync.PickLatestDailyFile(
            [
                File_("MSP2027050220241102D100.xlsx", new DateTime(2026, 11, 2, 5, 51, 0)),
                File_("MSP2027050320241103D100THERMO FISHER.xlsx", new DateTime(2026, 11, 3, 8, 0, 0)),
                File_("MSP2027050320241103L100.xlsx", new DateTime(2026, 11, 3, 5, 51, 0)),
                File_("PRD2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 55, 0)),
                File_("PPW Horizontal - WW45_031126.xlsx", new DateTime(2026, 11, 3, 6, 30, 0)),
            ], "D", "100", Now, TimeSpan.FromMinutes(5));

        picked!.Path.Should().EndWith("MSP2027050220241102D100.xlsx");
    }

    [Fact]
    public void PickLatestDailyFile_TwoFilesOnOneDay_TakesTheEarliest()
    {
        var picked = BuyerViewSync.PickLatestDailyFile(
            [
                File_("MSP2027060320241103D100.xlsx", new DateTime(2026, 11, 3, 8, 30, 0)), // re-run with other dates
                File_("MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0)),
            ], "D", "100", Now, TimeSpan.FromMinutes(5));

        picked!.Path.Should().EndWith("MSP2027050320241103D100.xlsx");
    }

    [Fact]
    public void PickLatestDailyFile_FileStillBeingWritten_IsLeftForTheNextRun()
    {
        var picked = BuyerViewSync.PickLatestDailyFile(
            [
                File_("MSP2027050220241102D100.xlsx", new DateTime(2026, 11, 2, 5, 51, 0)),
                File_("MSP2027050320241103D100.xlsx", Now.AddMinutes(-2)),
            ], "D", "100", Now, TimeSpan.FromMinutes(5));

        picked!.Path.Should().EndWith("MSP2027050220241102D100.xlsx");
    }

    // ── Divisions ────────────────────────────────────────────────────────────

    [Fact]
    public void Divisions_BoundFromConfiguration_AreNotDoubled()
    {
        // Configuration binding appends to a list; a default of ["D"] once made appsettings'
        // ["D"] sync Melbourne twice (local test, 2026-11-03).
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BuyerViewSync:Divisions:0"] = "D" })
            .Build()
            .GetSection(BuyerViewSyncOptions.SectionName)
            .Get<BuyerViewSyncOptions>()!;

        options.DivisionsToSync.Should().Equal("D");
    }

    [Fact]
    public void DivisionsToSync_NoneConfigured_IsMelbourne() =>
        new BuyerViewSyncOptions().DivisionsToSync.Should().Equal("D");

    [Fact]
    public void DivisionsToSync_RepeatsAndCase_AreNormalised() =>
        new BuyerViewSyncOptions { Divisions = ["d", "L", "D", " "] }.DivisionsToSync.Should().Equal("D", "L");

    // ── Runs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_NewFile_IsApplied()
    {
        AddFile("MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0), Lines(3));

        var result = Sync().Run().Single();

        result.Result.Should().Be(SyncRunResult.Applied);
        result.LinesNew.Should().Be(3);
        Count("SELECT COUNT(*) FROM Line;").Should().Be(3);
    }

    [Fact]
    public void Run_SameFileAgain_IsSkipped()
    {
        AddFile("MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0), Lines(3));
        Sync().Run();

        var result = Sync().Run().Single();

        result.Result.Should().Be(SyncRunResult.Skipped);
        Count("SELECT COUNT(*) FROM SyncRun;").Should().Be(1);
    }

    [Fact]
    public void Run_FileWithFarFewerRows_IsRefusedAndClosesNothing()
    {
        AddFile("MSP2027050220241102D100.xlsx", new DateTime(2026, 11, 2, 5, 51, 0), Lines(10));
        Sync().Run();
        AddFile("MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0), Lines(4));

        var result = Sync().Run().Single();

        result.Result.Should().Be(SyncRunResult.Failed);
        result.Message.Should().Contain("4 rows against 10");
        Count("SELECT COUNT(*) FROM Line WHERE ClosedDate IS NOT NULL;").Should().Be(0);
        Count("SELECT COUNT(*) FROM SyncRun WHERE Result = 'Failed';").Should().Be(1);
    }

    [Fact]
    public void Run_UnreadableFile_IsRecordedAsFailed()
    {
        _files.Add(new MspOutputCandidate(@"\\SRXAPP03\MSP-test\Output\MSP2027050320241103D100.xlsx", new DateTime(2026, 11, 3, 5, 57, 0)));
        _contents[@"\\SRXAPP03\MSP-test\Output\MSP2027050320241103D100.xlsx"] =
            () => throw new MspOutputFormatException("Not an MSP report: missing column(s) Buy Status in the header row.");

        var result = Sync().Run().Single();

        result.Result.Should().Be(SyncRunResult.Failed);
        result.Message.Should().Contain("Buy Status");
        Count("SELECT COUNT(*) FROM SyncRun WHERE Result = 'Failed';").Should().Be(1);
    }

    [Fact]
    public void Run_NoFileAtAll_IsSkipped()
    {
        var result = Sync().Run().Single();

        result.Result.Should().Be(SyncRunResult.Skipped);
        result.Message.Should().Contain("No daily MSP file");
    }
}
