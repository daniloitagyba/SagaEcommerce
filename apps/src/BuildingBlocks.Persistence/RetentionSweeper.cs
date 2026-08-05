using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BuildingBlocks;

// Milestone 63: outbox_messages/inbox_messages are append-only and nothing
// ever deleted from them - a real gap this table pair shares across
// Orders.Worker, Inventory.Service, and Payments.Service (all three grew
// past 100k+ rows and 100+MiB from this project's own load tests alone,
// verified against the live database before writing this). Shared here
// rather than duplicated three times because the cleanup is the exact
// same statement shape everywhere, just a different table/column name.
public sealed record RetentionTarget(string TableName, string TimestampColumn);

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public string ConnectionString { get; set; } = string.Empty;

    public IReadOnlyList<RetentionTarget> Targets { get; set; } = [];

    public int RetentionDays { get; set; } = 7;

    public int BatchSize { get; set; } = 1_000;

    public int SweepIntervalMinutes { get; set; } = 60;
}

public sealed class RetentionSweeper(IOptions<RetentionOptions> options, ILogger<RetentionSweeper> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Targets.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var target in _options.Targets)
            {
                try
                {
                    var deleted = await SweepAsync(target, stoppingToken);
                    if (deleted > 0)
                    {
                        RetentionLog.Swept(logger, target.TableName, deleted);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    RetentionLog.SweepFailed(logger, target.TableName, exception);
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(_options.SweepIntervalMinutes), stoppingToken);
        }
    }

    // Deletes in batches via ctid, not a single unbounded statement -
    // a one-shot "DELETE WHERE processed_at < cutoff" against a
    // multi-hundred-thousand-row table takes a long-held lock and a huge
    // single transaction; batching keeps each transaction short and lets
    // other writers interleave between batches.
    private async Task<long> SweepAsync(RetentionTarget target, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);
        long totalDeleted = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                DELETE FROM {target.TableName}
                WHERE ctid IN (
                    SELECT ctid FROM {target.TableName}
                    WHERE {target.TimestampColumn} IS NOT NULL AND {target.TimestampColumn} < $1
                    LIMIT $2
                )
                """;
            command.Parameters.AddWithValue(cutoff);
            command.Parameters.AddWithValue(_options.BatchSize);

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            totalDeleted += affected;

            if (affected < _options.BatchSize)
            {
                break;
            }
        }

        return totalDeleted;
    }
}

internal static partial class RetentionLog
{
    [LoggerMessage(EventId = 9800, Level = LogLevel.Information, Message = "Retention sweep deleted {DeletedCount} row(s) from {TableName}")]
    public static partial void Swept(ILogger logger, string tableName, long deletedCount);

    [LoggerMessage(EventId = 9801, Level = LogLevel.Error, Message = "Retention sweep of {TableName} failed")]
    public static partial void SweepFailed(ILogger logger, string tableName, Exception exception);
}
