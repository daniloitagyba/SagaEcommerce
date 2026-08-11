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
/// </summary>
public sealed class AntiEntropySweeper(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IOptions<AntiEntropyOptions> options,
    ILeaderElection leaderElection,
    ILogger<AntiEntropySweeper> logger) : BackgroundService
{
    private static readonly string[] PaymentAccountedStatuses =
        [OrderStatuses.Confirmed, OrderStatuses.Picking, OrderStatuses.Shipped, OrderStatuses.FulfillmentHold];

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

    /// <summary>Public so it can be driven directly, the same testable seam SagaTimeoutSweeper's own ResolveAsync already gives integration tests.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var paymentDivergences = await CheckOrdersHaveAccountedPaymentsAsync(cancellationToken);
        var backorderDivergences = await CheckBackordersBelongToWaitingOrdersAsync(cancellationToken);
        var committedInventoryDivergences = await CheckCommittedInventoryBelongsToLiveOrdersAsync(cancellationToken);

        AntiEntropyLog.SweepCompleted(logger, paymentDivergences, backorderDivergences, committedInventoryDivergences);
    }

    private async Task<int> CheckOrdersHaveAccountedPaymentsAsync(CancellationToken cancellationToken)
    {
        var candidates = await GetPaymentCandidateOrdersAsync(cancellationToken);
        var paymentsClient = httpClientFactory.CreateClient("anti-entropy-payments");
        var divergences = 0;

        foreach (var (orderId, orderStatus) in candidates)
        {
            string? paymentState;
            try
            {
                using var response = await paymentsClient.GetAsync($"/payments/by-order/{orderId:N}", cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    paymentState = null;
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var payload = await response.Content.ReadFromJsonAsync<PaymentLookupResponse>(cancellationToken);
                    paymentState = payload?.State;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Payments unreachable this tick is not evidence of a
                // divergence - it is evidence the sweep needs to try again
                // later, which the next tick already does.
                AntiEntropyLog.DependencyUnavailable(logger, "payments-service", exception);
                continue;
            }

            if (AntiEntropyChecks.OrderIsMissingAnAccountedPayment(paymentState))
            {
                divergences++;
                OrdersTelemetry.RecordAntiEntropyDivergence("order_missing_accounted_payment");
                AntiEntropyLog.PaymentDivergence(logger, orderId, orderStatus, paymentState ?? "none");
            }
        }

        return divergences;
    }

    private async Task<int> CheckBackordersBelongToWaitingOrdersAsync(CancellationToken cancellationToken)
    {
        var inventoryClient = httpClientFactory.CreateClient("anti-entropy-inventory");
        IReadOnlyList<BackorderResponse> backorders;
        try
        {
            using var response = await inventoryClient.GetAsync("/inventory/backorders", cancellationToken);
            response.EnsureSuccessStatusCode();
            backorders = await response.Content.ReadFromJsonAsync<List<BackorderResponse>>(cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AntiEntropyLog.DependencyUnavailable(logger, "inventory-service", exception);
            return 0;
        }

        var divergences = 0;
        var orderIds = backorders.Select(b => b.OrderId).Distinct().Take(_options.BatchSize).ToList();
        var statuses = await GetOrderStatusesAsync(orderIds, cancellationToken);

        foreach (var backorder in backorders)
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
            using var response = await inventoryClient.GetAsync("/inventory/committed-reservations", cancellationToken);
            response.EnsureSuccessStatusCode();
            committedReservations = await response.Content.ReadFromJsonAsync<List<CommittedReservationResponse>>(cancellationToken) ?? [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AntiEntropyLog.DependencyUnavailable(logger, "inventory-service", exception);
            return 0;
        }

        var divergences = 0;
        var orderIds = committedReservations.Select(r => r.OrderId).Distinct().Take(_options.BatchSize).ToList();
        var statuses = await GetOrderStatusesAsync(orderIds, cancellationToken);

        foreach (var reservation in committedReservations)
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

    private async Task<IReadOnlyList<(Guid OrderId, string Status)>> GetPaymentCandidateOrdersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, status FROM orders
            WHERE status = ANY(@statuses) AND payment_method IS NOT NULL
            ORDER BY created_at DESC
            LIMIT @batch_size
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Varchar, PaymentAccountedStatuses);
        command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _options.BatchSize);

        var results = new List<(Guid, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return results;
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

    private sealed record PaymentLookupResponse(Guid OrderId, string State, decimal Amount, string Currency);

    private sealed record BackorderResponse(Guid ReservationId, Guid OrderId, string Sku, int Quantity, DateTimeOffset RequestedAt);

    private sealed record CommittedReservationResponse(Guid ReservationId, Guid OrderId, string Sku, int Quantity, DateTimeOffset CommittedAt);
}

public sealed partial class AntiEntropyLog
{
    [LoggerMessage(EventId = 9300, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} (status {OrderStatus}) has no accounted payment - Payments reports {PaymentState}")]
    public static partial void PaymentDivergence(ILogger logger, Guid orderId, string orderStatus, string paymentState);

    [LoggerMessage(EventId = 9301, Level = LogLevel.Warning, Message = "Anti-entropy: backorder for sku {Sku} references order {OrderId}, whose status is {OrderStatus} - not Backordered")]
    public static partial void BackorderDivergence(ILogger logger, Guid orderId, string sku, string orderStatus);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Anti-entropy sweep completed: {PaymentDivergences} payment divergence(s), {BackorderDivergences} backorder divergence(s), {CommittedInventoryDivergences} committed-inventory divergence(s)")]
    public static partial void SweepCompleted(ILogger logger, int paymentDivergences, int backorderDivergences, int committedInventoryDivergences);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Error, Message = "Anti-entropy sweep failed; will retry next tick")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9304, Level = LogLevel.Warning, Message = "Anti-entropy: {Dependency} unavailable this tick - skipped, not counted as a divergence")]
    public static partial void DependencyUnavailable(ILogger logger, string dependency, Exception exception);

    [LoggerMessage(EventId = 9305, Level = LogLevel.Warning, Message = "Anti-entropy: order {OrderId} still has {Quantity} unit(s) of sku {Sku} committed - order status is {OrderStatus}, not a status that should still hold it")]
    public static partial void CommittedInventoryDivergence(ILogger logger, Guid orderId, string sku, int quantity, string orderStatus);
}
