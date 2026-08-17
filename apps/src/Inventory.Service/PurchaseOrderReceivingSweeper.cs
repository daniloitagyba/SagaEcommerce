using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace Inventory.Service;

/// <summary>Turns a Requested purchase order into an actual restock once its lead time has elapsed.</summary>
public sealed class PurchaseOrderReceivingSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<ReplenishmentOptions> options,
    TimeProvider timeProvider,
    ILogger<PurchaseOrderReceivingSweeper> logger,
    ResiliencePipelineProvider<string> pipelineProvider) : BackgroundService
{
    /// <summary>Numerically distinct from BackorderTimeoutSweeper's and Payments' lock keys.</summary>
    private const long SweepLockKey = 7400_0002;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ReplenishmentOptions _options = options.Value;
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.PostgresTransactionPipeline);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.ReceivingSweepIntervalSeconds);

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
                ReplenishmentLog.ReceivingSweepFailed(logger, exception);
            }
        }
    }

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
            var cutoff = now - TimeSpan.FromSeconds(_options.LeadTimeSeconds);

            var claimedIds = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT id AS "Value"
                    FROM purchase_orders
                    WHERE state = {PurchaseOrderStates.Requested} AND requested_at <= {cutoff}
                    ORDER BY requested_at
                    LIMIT {_options.ReceivingSweepBatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            if (claimedIds.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var purchaseOrders = await dbContext.PurchaseOrders
                .Where(purchaseOrder => claimedIds.Contains(purchaseOrder.Id))
                .ToListAsync(ct);

            foreach (var purchaseOrder in purchaseOrders)
            {
                if (!purchaseOrder.TryReceive(now))
                {
                    continue;
                }

                var request = new InventoryRestockRequested(
                    Guid.NewGuid(), purchaseOrder.Id, purchaseOrder.Sku, purchaseOrder.Quantity, purchaseOrder.CorrelationId, now,
                    purchaseOrder.WarehouseCode);

                dbContext.OutboxMessages.Add(OutboxMessage.Create(
                    Guid.NewGuid(),
                    nameof(InventoryRestockRequested),
                    JsonSerializer.Serialize(request, SerializerOptions),
                    now,
                    purchaseOrder.CorrelationId,
                    Activity.Current?.Id,
                    Activity.Current?.TraceStateString));

                ReplenishmentLog.Received(logger, purchaseOrder.Id, purchaseOrder.Sku, purchaseOrder.WarehouseCode, purchaseOrder.Quantity, purchaseOrder.CorrelationId);
            }

            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }, cancellationToken);
    }
}
