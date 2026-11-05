using Microsoft.Extensions.Logging;
using Reporting.Infrastructure.Domains.SupplyChain.MspOutput;

namespace Reporting.Infrastructure.Domains.SupplyChain.BuyerView;

/// <summary>Settings for the buyer view sync (section "BuyerViewSync").</summary>
public sealed class BuyerViewSyncOptions
{
    public const string SectionName = "BuyerViewSync";

    /// <summary>Off by default, so a local run never syncs unless asked to.</summary>
    public bool Enabled { get; set; }

    /// <summary>The PHP MSP's Output folder on SRXAPP03, read-only (on the server: \\SRXAPP03\MSPOutput$).</summary>
    public string OutputFolder { get; set; } = string.Empty;

    /// <summary>
    /// Account the service connects to the share as (SRXAPP03\svc-msp-read). Empty = use the
    /// process's own access (a developer PC). See <see cref="NetworkShareConnection"/>.
    /// </summary>
    public string ShareUser { get; set; } = string.Empty;

    /// <summary>Password for <see cref="ShareUser"/>. From the secrets file only; never logged.</summary>
    public string SharePassword { get; set; } = string.Empty;

    /// <summary>The buyer view's SQLite file, e.g. E:\data\supplychain.db.</summary>
    public string DatabasePath { get; set; } = string.Empty;

    /// <summary>
    /// Divisions to sync (D = Melbourne, L = Johor Bahru). Empty by default: configuration binding
    /// appends to a list rather than replacing it, so a default here would be synced twice once
    /// appsettings also lists it. Use <see cref="DivisionsToSync"/>.
    /// </summary>
    public List<string> Divisions { get; set; } = [];

    /// <summary>The configured divisions without repeats, or Melbourne (D) when none are configured.</summary>
    public IReadOnlyList<string> DivisionsToSync =>
        Divisions.Count == 0
            ? ["D"]
            : Divisions.Select(d => d.Trim().ToUpperInvariant()).Where(d => d.Length > 0).Distinct().ToList();

    public string Company { get; set; } = "100";

    /// <summary>A file written less than this long ago may still be being written by MSP; wait for the next run.</summary>
    public int MinFileAgeMinutes { get; set; } = 5;

    /// <summary>
    /// A file with fewer rows than this share of the previous applied file is refused, so a
    /// broken MSP run can't close hundreds of lines at once.
    /// </summary>
    public double MinRowRatio { get; set; } = 0.5;

    /// <summary>How often the in-process timer checks whether to sync.</summary>
    public int CheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Server local time window in which the timer syncs. SRXWEBAPP1 and SRXAPP03 both run on
    /// Melbourne time (Hector, 2026-11-03). MSP's daily run starts at 05:30 on SRXAPP03 and its
    /// files land about 05:50–05:57 (28 Oct – 3 Nov 2026); checks after a file is applied are
    /// skipped at no cost. The window runs to 09:30 so a late MSP run (e.g. Ampps restarted by
    /// hand after Windows updates) is still picked up the same morning.
    /// </summary>
    public string WindowStart { get; set; } = "05:45";

    public string WindowEnd { get; set; } = "09:30";
}

/// <summary>An MSP file in the Output folder, with the time it was last written (server local time).</summary>
public sealed record MspOutputCandidate(string Path, DateTime WrittenAt);

/// <summary>
/// Applies the newest daily MSP file for each division to the buyer view store.
///
/// File choice: prefix MSP for the division and company, no customer (ad-hoc customer runs
/// skipped), from the latest day there is one. A browser run with the scheduled run's dates
/// writes the same file name and replaces it; a re-run with other dates writes a second file, so
/// on such a day the earliest one is taken. The run date is the file's write date.
///
/// Reads the Output folder only. Buyer entries in the store are never changed by a sync.
/// </summary>
public sealed class BuyerViewSync(
    BuyerViewSyncOptions options,
    BuyerViewStore store,
    Func<string, string, string, IEnumerable<MspOutputCandidate>> listFiles,
    Func<string, MspOutputFile> readFile,
    TimeProvider clock,
    ILogger<BuyerViewSync> logger)
{
    /// <summary>
    /// Lists one division's MSP files in a folder with their write times. The folder holds every
    /// report since 2017 (about 12,700 files), so the pattern is narrowed to the division and company.
    /// </summary>
    public static IEnumerable<MspOutputCandidate> ListFolder(string folder, string division, string company) =>
        new DirectoryInfo(folder)
            .EnumerateFiles($"MSP*{division}{company}*.xlsx", SearchOption.TopDirectoryOnly)
            .Select(f => new MspOutputCandidate(f.FullName, f.LastWriteTime));

    /// <summary>
    /// Picks the file to sync: the first daily run on the latest day that has one, ignoring files
    /// written less than <paramref name="minAge"/> before <paramref name="now"/>.
    /// </summary>
    public static MspOutputCandidate? PickLatestDailyFile(
        IEnumerable<MspOutputCandidate> candidates, string division, string company, DateTime now, TimeSpan minAge) =>
        candidates
            .Where(c => now - c.WrittenAt >= minAge)
            .Where(c => MspOutputFileName.TryParse(c.Path, out var name)
                        && name!.Prefix == "MSP" && name.IsDailyRun
                        && name.Division.Equals(division, StringComparison.OrdinalIgnoreCase)
                        && name.Company == company)
            .GroupBy(c => DateOnly.FromDateTime(c.WrittenAt))
            .OrderByDescending(g => g.Key)
            .Select(g => g.OrderBy(c => c.WrittenAt).First())
            .FirstOrDefault();

    public IReadOnlyList<SyncRunResult> Run()
    {
        store.EnsureSchema();
        var results = new List<SyncRunResult>();
        foreach (var division in options.DivisionsToSync)
            results.Add(RunDivision(division));
        return results;
    }

    private SyncRunResult RunDivision(string division)
    {
        var startedUtc = clock.GetUtcNow().UtcDateTime;
        var now = clock.GetLocalNow().DateTime;

        var file = PickLatestDailyFile(listFiles(options.OutputFolder, division, options.Company), division, options.Company, now,
            TimeSpan.FromMinutes(options.MinFileAgeMinutes));
        if (file is null)
        {
            logger.LogWarning("[BuyerViewSync] No daily MSP file for {Division}{Company} in {Folder}",
                division, options.Company, options.OutputFolder);
            return new SyncRunResult(division, string.Empty, SyncRunResult.Skipped, "No daily MSP file found.");
        }

        var fileName = Path.GetFileName(file.Path);
        var runDate = DateOnly.FromDateTime(file.WrittenAt);
        if (store.WasApplied(fileName, file.WrittenAt))
            return new SyncRunResult(division, fileName, SyncRunResult.Skipped, "Already applied.");

        SyncRunResult result;
        try
        {
            var lines = readFile(file.Path).Lines;
            var previous = store.LastAppliedRowCount(division);
            if (lines.Count == 0 || (previous is > 0 && lines.Count < previous * options.MinRowRatio))
            {
                result = new SyncRunResult(division, fileName, SyncRunResult.Failed,
                    $"Refused: {lines.Count} rows against {previous} in the last applied file (minimum ratio {options.MinRowRatio}).",
                    lines.Count);
                store.RecordRun(result, file.WrittenAt, runDate, startedUtc, clock.GetUtcNow().UtcDateTime);
            }
            else
            {
                result = store.ApplySnapshot(division, fileName, file.WrittenAt, runDate, lines,
                    startedUtc, clock.GetUtcNow().UtcDateTime);
            }
        }
        catch (Exception ex) when (ex is MspOutputFormatException or IOException or UnauthorizedAccessException)
        {
            result = new SyncRunResult(division, fileName, SyncRunResult.Failed, ex.Message);
            store.RecordRun(result, file.WrittenAt, runDate, startedUtc, clock.GetUtcNow().UtcDateTime);
        }

        if (result.Result == SyncRunResult.Applied)
            logger.LogInformation(
                "[BuyerViewSync] {Division} {File}: {Rows} rows, {New} new, {Closed} closed, {Reopened} reopened",
                division, fileName, result.RowsRead, result.LinesNew, result.LinesClosed, result.LinesReopened);
        else
            logger.LogWarning("[BuyerViewSync] {Division} {File}: {Result} — {Message}",
                division, fileName, result.Result, result.Message);

        return result;
    }
}
