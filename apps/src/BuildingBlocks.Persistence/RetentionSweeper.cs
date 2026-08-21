using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BuildingBlocks;

/// <summary>A table subject to periodic retention pruning.</summary>
/// <param name="RetentionDaysOverride">Overrides the service-wide RetentionDays for this target; null falls back to the default.</param>
public sealed record RetentionTarget(string TableName, string TimestampColumn, int? RetentionDaysOverride = null);

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public string ConnectionString { get; set; } = string.Empty;

    public IReadOnlyList<RetentionTarget> Targets { get; set; } = [];

    public int RetentionDays { get; set; } = 7;

    public int BatchSize { get; set; } = 1_000;

    public int SweepIntervalMinutes { get; set; } = 60;
}

public sealed class RetentionSweeper(
    IOptions<RetentionOptions> options,
    ILogger<RetentionSweeper> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

            await Task.Delay(TimeSpan.FromMinutes(_options.SweepIntervalMinutes), _timeProvider, stoppingToken);
        }
    }

    private async Task<long> SweepAsync(RetentionTarget target, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var cutoff = _timeProvider.GetUtcNow().AddDays(-(target.RetentionDaysOverride ?? _options.RetentionDays));
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
