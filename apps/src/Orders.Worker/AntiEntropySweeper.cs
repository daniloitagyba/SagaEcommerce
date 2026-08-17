using System.Net.Http.Json;
using BuildingBlocks;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Orders.Worker;

/// <summary>
/// Detects order consistency divergences.
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
        var batch = backorders.Take(_options.BatchSize).ToList();
        var orderIds = batch.Select(b => b.OrderId).Distinct().ToList();
        var statuses = await GetOrderStatusesAsync(orderIds, cancellationToken);

        foreach (var backorder in batch)
        {
            if (!statuses.TryGetValue(backorder.OrderId, out var orderStatus))
            {
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
    /// Detects committed inventory for cancelled orders.
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

    /// <summary>Matches InventoryEndpoints' { items, total, skip, limit } envelope.</summary>
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
