using System.Data.Odbc;
using Dapper;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;

namespace Reporting.Infrastructure.Pipeline;

/// <summary>
/// Base class for all fetchers that query IBM i AS/400 DB2 via ODBC.
///
/// Critical ODBC rules (see ai/memory/03-data-access-patterns.md):
///   - Positional parameters only: use ? not @name
///   - Schema prefix required: {schema}.{table} (e.g., mvxcdta.MITFAC)
///   - String fields are CHAR (fixed-width) — always TRIM() in SELECT
///   - MOVEX date fields are INT YYYYMMDD — use MovexDateConverter
///
/// Polly policy (ADR-007 / Gap 3):
///   - Retry: 3 attempts, exponential backoff 1s / 2s / 4s
///   - CircuitBreaker: 5 failures → 30s open
///
/// Uses skill: data/report-generation v1.0
/// Uses skill: integration/movex-db2-data-source v1.0
/// Uses skill: architecture/resilience-patterns v1.0
/// </summary>
public abstract class Db2DirectFetcher : IDataFetcher
{
    private readonly ResiliencePipeline _resiliencePipeline;

    protected readonly string ConnectionString;
    protected readonly string Schema;
    protected readonly int CommandTimeoutSeconds;
    protected readonly ILogger Logger;

    public string DataSource => "Db2Direct";
    public abstract string ReportId { get; }

    protected Db2DirectFetcher(
        string connectionString,
        string schema,
        int commandTimeoutSeconds,
        ILogger logger)
    {
        ConnectionString = connectionString;
        Schema = schema;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        Logger = logger;

        // ADR-007: Polly built from scratch — not inherited from any template
        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder()
                    .Handle<OdbcException>()
                    .Handle<TimeoutException>()
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30)
            })
            .Build();
    }

    /// <summary>
    /// T12a: ExecutedAtUtc is stamped here — at the start of the fetch stage — not when
    /// the query completes. This ensures the timestamp reflects when the data snapshot began,
    /// which is what appears in PDF headers ("Data as at: ...") and Excel header rows.
    /// </summary>
    public async Task<ReportDataSet> FetchAsync(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        CancellationToken cancellationToken = default)
    {
        // T12a: stamp ExecutedAtUtc at fetch start, not completion
        var executedAtUtc = DateTime.UtcNow;

        Logger.LogInformation(
            "Fetching {ReportId} from DB2 schema={Schema} executedAt={ExecutedAt:O}",
            ReportId, Schema, executedAtUtc);

        ReportDataSet dataSet = null!;

        await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            using var connection = new OdbcConnection(ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            dataSet = await FetchCoreAsync(connection, report, parameters, executedAtUtc, ct)
                           .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return dataSet;
    }

    /// <summary>
    /// Derived fetchers implement this method.
    /// The connection is already open. executedAtUtc is pre-stamped — pass it to ReportDataSet.
    /// Use positional parameters (?) and schema-prefixed table names.
    /// </summary>
    protected abstract Task<ReportDataSet> FetchCoreAsync(
        OdbcConnection connection,
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> parameters,
        DateTime executedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Helper: executes a Dapper query with command timeout and cancellation token.
    /// </summary>
    protected async Task<IEnumerable<T>> QueryAsync<T>(
        OdbcConnection connection,
        string sql,
        object? param = null,
        CancellationToken cancellationToken = default)
    {
        var cmd = new CommandDefinition(
            commandText: sql,
            parameters: param,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken);

        return await connection.QueryAsync<T>(cmd).ConfigureAwait(false);
    }
}
