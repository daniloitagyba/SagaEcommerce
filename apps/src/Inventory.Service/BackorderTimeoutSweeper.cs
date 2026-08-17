using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace Inventory.Service;

/// <summary>Times out backorders nobody restocked in time and reports permanent refusal back to the saga.</summary>
public sealed class BackorderTimeoutSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<BackorderOptions> options,
    TimeProvider timeProvider,
    ILogger<BackorderTimeoutSweeper> logger,
    ResiliencePipelineProvider<string> pipelineProvider) : BackgroundService
{
    /// <summary>Numerically distinct from Payments' SweepLockKey.</summary>
    private const long SweepLockKey = 7400_0001;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly BackorderOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.TimeoutSweepIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                BackorderSweeperLog.SweepFailed(logger, exception);
            }
        }
    }

    /// <summary>Runs one sweep pass; public so integration tests can drive it directly.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await _pipeline.ExecuteAsync(async ct =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

            var isSweeper = await dbContext.Database
                .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({SweepLockKey}) AS \"Value\"")
                .SingleAsync(ct);

            if (!isSweeper)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var now = timeProvider.GetUtcNow();
            var cutoff = now - TimeSpan.FromMinutes(_options.TimeoutMinutes);

            var candidateSkus = await dbContext.Backorders
                .Where(backorder => backorder.RequestedAt <= cutoff)
                .Select(backorder => backorder.Sku)
                .Distinct()
                .OrderBy(sku => sku)
                .Take(_options.TimeoutSweepBatchSize)
                .ToListAsync(ct);

            if (candidateSkus.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var timedOut = 0;
            foreach (var sku in candidateSkus)
            {
                await SkuAdvisoryLock.AcquireAsync(dbContext, sku, ct);

                var pending = await dbContext.Backorders
                    .Where(backorder => backorder.Sku == sku && backorder.RequestedAt <= cutoff)
                    .ToListAsync(ct);

                foreach (var backorder in pending)
                {
                    dbContext.Backorders.Remove(backorder);

                    var reply = new InventoryReservationReplied(
                        backorder.ReservationId,
                        backorder.OrderId,
                        backorder.Sku,
                        backorder.Quantity,
                        Reserved: false,
                        Reason: "backorder timed out without restock",
                        backorder.CorrelationId,
                        now,
                        Backordered: false);

                    dbContext.OutboxMessages.Add(OutboxMessage.Create(
                        Guid.NewGuid(),
                        nameof(InventoryReservationReplied),
                        JsonSerializer.Serialize(reply, SerializerOptions),
                        now,
                        backorder.CorrelationId,
                        Activity.Current?.Id,
                        Activity.Current?.TraceStateString));

                    timedOut++;
                }
            }

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            if (timedOut > 0)
            {
                BackorderSweeperLog.TimedOut(logger, timedOut);
            }
        }, cancellationToken);
    }
}

public sealed partial class BackorderSweeperLog
{
    [LoggerMessage(EventId = 9020, Level = LogLevel.Error, Message = "Backorder timeout sweep failed; backorders it did not time out remain claimable next pass")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Warning, Message = "Timed out {Count} backorder(s) that outlasted their wait window without a restock")]
    public static partial void TimedOut(ILogger logger, int count);
}
