using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Inventory.Service.Data;
using Inventory.Service.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Service;

/// <summary>
/// Milestone 89: the other half of ReplenishmentRequestProcessor - turns a
/// Requested purchase order into an actual restock once its lead time has
/// elapsed. Single-sweeper via pg_try_advisory_xact_lock, the same
/// reasoning BackorderTimeoutSweeper already carries in this service: the
/// lock only stops every replica polling every tick, since SKIP LOCKED
/// already makes concurrent sweeps safe on its own.
/// </summary>
public sealed class PurchaseOrderReceivingSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<ReplenishmentOptions> options,
    ILogger<PurchaseOrderReceivingSweeper> logger) : BackgroundService
{
    /// <summary>Kept numerically distinct from BackorderTimeoutSweeper's and Payments' lock keys so a grep for any of them is unambiguous.</summary>
    private const long SweepLockKey = 7400_0002;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ReplenishmentOptions _options = options.Value;

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
                // A failed sweep must never take the host down - purchase orders it missed this pass are still claimable next time.
                ReplenishmentLog.ReceivingSweepFailed(logger, exception);
            }
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var isSweeper = await dbContext.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({SweepLockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!isSweeper)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
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
            .ToListAsync(cancellationToken);

        if (claimedIds.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var purchaseOrders = await dbContext.PurchaseOrders
            .Where(purchaseOrder => claimedIds.Contains(purchaseOrder.Id))
            .ToListAsync(cancellationToken);

        foreach (var purchaseOrder in purchaseOrders)
        {
            if (!purchaseOrder.TryReceive(now))
            {
                continue;
            }

            // OrderId is a real customer order for every other producer of
            // this command (a return); there is none here, so the purchase
            // order's own id fills the slot instead - ProcessRestockAsync's
            // shared validator requires it non-empty, and reusing it (rather
            // than a throwaway Guid.NewGuid()) keeps the resulting
            // InventoryRestockReplied traceable back to the purchase order
            // that caused it.
            var request = new InventoryRestockRequested(
                Guid.NewGuid(), purchaseOrder.Id, purchaseOrder.Sku, purchaseOrder.Quantity, purchaseOrder.CorrelationId, now);

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

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
