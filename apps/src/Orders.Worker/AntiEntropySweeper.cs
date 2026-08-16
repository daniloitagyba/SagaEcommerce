using System.Net.Http.Json;
using BuildingBlocks;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Catches the class of bug a prior audit found by
/// reading code, this time by comparing what actually happened - two
/// invariants that should always hold across the Orders/Payments and
/// Orders/Inventory boundaries, checked periodically rather than assumed.
/// The decision logic itself lives in BuildingBlocks' AntiEntropyChecks,
/// pure and unit-tested without a database or an HTTP call in sight; this
/// class's only job is gathering the two facts each check compares and
/// recording what it finds.
///
/// Single-sweeper via LeaderElectionService, the same Kubernetes
/// Lease-based coordination SagaTimeoutSweeper already uses in this same
/// process - a second, independent advisory-lock-based mechanism was not
/// invented for this, since Orders.Worker already pays the cost of the
/// Lease-based one.
///
/// A divergence is logged and counted (anti_entropy.divergences), never
/// auto-corrected. Guessing which side is wrong from here would repeat
/// exactly the mistake this sweep exists to catch - a human, or a
/// separately-reasoned compensation (the cancellation-compensation fix, for the one
/// bug class this happens to have already been built for) decides what to
/// do about a divergence; this sweep's job stops at making sure one is
/// never silent.
///
/// Split across two files to stay under the 500-line module-size budget,
/// the same physical-split-not-different-concern pattern
/// SagaOrchestrationStore uses for its own split: this file owns the loop,
/// the two Inventory-sourced checks (backorder, committed-inventory -
/// already paginated through Inventory.Service's own endpoints, so they
/// need no cursor of their own) and the shared order-status lookup;
/// AntiEntropySweeper.Cursor.cs owns the three Orders-database-local
/// checks that walk their table via the durable cursor in
/// anti_entropy_progress - see that file's own comment and
/// docs/roadmap-milestones-91-99.md, "the anti-entropy sweep can only ever
/// see the newest rows".
/// </summary>
public sealed partial class AntiEntropySweeper(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IOptions<AntiEntropyOptions> options,
    ILeaderElection leaderElection,
    TimeProvider timeProvider,
    ILogger<AntiEntropySweeper> logger) : BackgroundService
{
    private static readonly string[] PaymentAccountedStatuses =
        [OrderStatuses.Confirmed, OrderStatuses.Picking, OrderStatuses.Shipped, OrderStatuses.FulfillmentHold];

    // The two statuses a saga is ever mid-flight for - Created while any
    // of the four steps is in progress, Backordered while parked waiting
    // on a restock (SagaOrchestrationState.ParkedAt keeps the row present,
    // not deleted, for that whole wait - see SagaTimeoutSweeper's own
    // comment). Every other status is one the saga has already finished
    // with and correctly has no row left for.
    private static readonly string[] StuckCandidateStatuses = [OrderStatuses.Created, OrderStatuses.Backordered];

    private const string PaymentCheckName = "payment-accounted";
    private const string WriteReadModelCheckName = "write-read-model";

    private readonly AntiEntropyOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.SweepIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!leaderElection.IsLeader)
            {
                continue;
            }

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AntiEntropyLog.SweepFailed(logger, exception);
            }
        }
    }

    /// <summary>Public so it can be driven directly, the same testable seam SagaTimeoutSweeper's own SweepOnceAsync already gives integration tests.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var paymentDivergences = await CheckOrdersHaveAccountedPaymentsAsync(cancellationToken);
        var backorderDivergences = await CheckBackordersBelongToWaitingOrdersAsync(cancellationToken);
        var committedInventoryDivergences = await CheckCommittedInventoryBelongsToLiveOrdersAsync(cancellationToken);
        var writeReadModelDivergences = await CheckWriteModelMatchesReadModelAsync(cancellationToken);
        var stuckOrderDivergences = await CheckOrdersStuckWithoutASagaRowAsync(cancellationToken);

        AntiEntropyLog.SweepCompleted(
            logger, paymentDivergences, backorderDivergences, committedInventoryDivergences,
            writeReadModelDivergences, stuckOrderDivergences);
    }

    private async Task<int> CheckBackordersBelongToWaitingOrdersAsync(CancellationToken cancellationToken)
    {
        var inventoryClient = httpClientFactory.CreateClient("anti-entropy-inventory");
        IReadOnlyList<BackorderResponse> backorders;
        try
        {
            using var response = await inventoryClient.GetAsync($"/inventory/backorders?limit={_options.BatchSize}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<InventoryPageResponse<BackorderResponse>>(cancellationToken);
            backorders = page?.Items ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AntiEntropyLog.DependencyUnavailable(logger, "inventory-service", exception);
            return 0;
        }

        var divergences = 0;
        // Inventory.Service now bounds this server-side (?limit= above), so
        // this is a defensive backstop, not the correctness-critical
        // mechanism it used to be: it used to apply Take only to the
        // status-lookup query, not the loop below, so every backorder past
        // position BatchSize found nothing in statuses and TryGetValue
        // reported it as "no such order" - a false divergence. The deferred
        // ones (now: anything past what a single page returns) are still
        // picked up on the next tick.
        var batch = backorders.Take(_options.BatchSize).ToList();
        var orderIds = batch.Select(b => b.OrderId).Distinct().ToList();
        var statuses = await GetOrderStatusesAsync(orderIds, cancellationToken);

        foreach (var backorder in batch)
        {
            if (!statuses.TryGetValue(backorder.OrderId, out var orderStatus))
            {
                // No matching order at all is its own, worse divergence -
                // still counted, under the same check, since the
                // consequence (a backorder that will never legitimately
                // clear) is identical either way.
                divergences++;
                OrdersTelemetry.RecordAntiEntropyDivergence("backorder_on_dead_order");
                AntiEntropyLog.BackorderDivergence(logger, backorder.OrderId, backorder.Sku, "unknown (no such order)");
                continue;
            }

            if (AntiEntropyChecks.BackorderBelongsToAnOrderNoLongerWaiting(orderStatus))
            {
                divergences++;
                OrdersTelemetry.RecordAntiEntropyDivergence("backorder_on_dead_order");
                AntiEntropyLog.BackorderDivergence(logger, backorder.OrderId, backorder.Sku, orderStatus);
            }
        }

        return divergences;
    }

    /// <summary>
    /// The check the audit above
    /// wanted from the start - committed
    /// inventory belonging to a cancelled order - built now that
    /// Inventory.Service's reservation ledger survives settlement to ask
    /// about. Same shape as the backorder check above: fetch what
    /// Inventory has on file, compare against Orders' own status column.
    /// </summary>
    private async Task<int> CheckCommittedInventoryBelongsToLiveOrdersAsync(CancellationToken cancellationToken)
    {
        var inventoryClient = httpClientFactory.CreateClient("anti-entropy-inventory");
        IReadOnlyList<CommittedReservationResponse> committedReservations;
        try
        {
            using var response = await inventoryClient.GetAsync($"/inventory/committed-reservations?limit={_options.BatchSize}", cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<InventoryPageResponse<CommittedReservationResponse>>(cancellationToken);
            committedReservations = page?.Items ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AntiEntropyLog.DependencyUnavailable(logger, "inventory-service", exception);
            return 0;
        }

        var divergences = 0;
        // Same fix as CheckBackordersBelongToWaitingOrdersAsync above -
        // Take has to bound the batch itself, not just the lookup.
        var batch = committedReservations.Take(_options.BatchSize).ToList();
        var orderIds = batch.Select(r => r.OrderId).Distinct().ToList();
        var statuses = await GetOrderStatusesAsync(orderIds, cancellationToken);

        foreach (var reservation in batch)
        {
            statuses.TryGetValue(reservation.OrderId, out var orderStatus);

            if (AntiEntropyChecks.CommittedInventoryBelongsToACancelledOrder(orderStatus))
            {
                divergences++;
                OrdersTelemetry.RecordAntiEntropyDivergence("committed_inventory_on_cancelled_order");
                AntiEntropyLog.CommittedInventoryDivergence(logger, reservation.OrderId, reservation.Sku, reservation.Quantity, orderStatus ?? "unknown (no such order)");
            }
        }

        return divergences;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> GetOrderStatusesAsync(List<Guid> orderIds, CancellationToken cancellationToken)
    {
        if (orderIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        const string sql = "SELECT id, status FROM orders WHERE id = ANY(@ids)";

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, orderIds.ToArray());

        var results = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results[reader.GetGuid(0)] = reader.GetString(1);
        }

        return results;
    }

    private sealed record BackorderResponse(Guid ReservationId, Guid OrderId, string Sku, int Quantity, DateTimeOffset RequestedAt);

    private sealed record CommittedReservationResponse(Guid ReservationId, Guid OrderId, string Sku, int Quantity, DateTimeOffset CommittedAt);

    /// <summary>Matches InventoryEndpoints' { items, total, skip, limit } envelope - same shape ProductEndpoints already uses in Catalog.Service.</summary>
    private sealed record InventoryPageResponse<T>(IReadOnlyList<T> Items, int Total, int Skip, int Limit);
}

public sealed partial class AntiEntropyLog
{
    [LoggerMessage(EventId = 9300, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} (status {OrderStatus}) has no accounted payment - Payments reports {PaymentState}")]
    public static partial void PaymentDivergence(ILogger logger, Guid orderId, string orderStatus, string paymentState);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Warning, Message = "Anti-entropy: backorder for sku {Sku} references order {OrderId}, whose status is {OrderStatus} - not Backordered")]
    public static partial void BackorderDivergence(ILogger logger, Guid orderId, string sku, string orderStatus);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Anti-entropy sweep completed: {PaymentDivergences} payment divergence(s), {BackorderDivergences} backorder divergence(s), {CommittedInventoryDivergences} committed-inventory divergence(s), {WriteReadModelDivergences} write/read-model divergence(s), {StuckOrderDivergences} stuck-order divergence(s)")]
    public static partial void SweepCompleted(ILogger logger, int paymentDivergences, int backorderDivergences, int committedInventoryDivergences, int writeReadModelDivergences, int stuckOrderDivergences);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Error, Message = "Anti-entropy sweep failed; will retry next tick")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9304, Level = LogLevel.Warning, Message = "Anti-entropy: {Dependency} unavailable this tick - skipped, not counted as a divergence")]
    public static partial void DependencyUnavailable(ILogger logger, string dependency, Exception exception);

    [LoggerMessage(EventId = 9305, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} still has {Quantity} unit(s) of sku {Sku} committed - order status is {OrderStatus}, not a status that should still hold it")]
    public static partial void CommittedInventoryDivergence(ILogger logger, Guid orderId, string sku, int quantity, string orderStatus);

    [LoggerMessage(EventId = 9306, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} write model reports {OrderStatus} but the read model (order_summaries) still reports {SummaryStatus}")]
    public static partial void WriteReadModelDivergence(ILogger logger, Guid orderId, string orderStatus, string summaryStatus);

    [LoggerMessage(EventId = 9307, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} (status {OrderStatus}) has no saga_orchestration_states row - likely stranded by a crash between saga completion and status resolution")]
    public static partial void StuckOrderDivergence(ILogger logger, Guid orderId, string orderStatus);
}
