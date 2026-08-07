using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Milestone 22's compensation half, extended in Milestone 43 into the
/// driver of a 4-step saga. One consumer subscribing to all four reply
/// topics and dispatching by topic name, since only one reply is ever
/// outstanding per order at a time.
///
/// State machine:
///   ReserveInventory  --(reserved)-->     DecidePayment   --(approved)--> CommitInventory --> done (Confirmed)
///   ReserveInventory  --(insufficient)--> done (RejectedInsufficientStock)
///   DecidePayment     --(declined)-->     ReleaseInventory (the compensation) --> done (RejectedPaymentDeclined)
/// </summary>
public sealed class OrderSagaReplyConsumer(
    IOptions<SagaOrchestrationOptions> options,
    IProducer<string, string> producer,
    SagaOrchestrationStore store,
    OrderStatusStore orderStatusStore,
    IOrderCacheInvalidator cacheInvalidator,
    IBestsellersStore bestsellersStore,
    ICatalogClient catalogClient,
    ILogger<OrderSagaReplyConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SagaOrchestrationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ReplyConsumerGroup,
            ClientId = $"{_options.ClientId}-reply",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = 1_000,
            AllowAutoCreateTopics = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(
        [
            _options.ReservationRepliedTopic,
            _options.DecisionRepliedTopic,
            _options.CommitRepliedTopic,
            _options.ReleaseRepliedTopic
        ]);
        SagaOrchestratorLog.Started(logger, _options.ReplyConsumerGroup, _options.ReplyConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> consumeResult;
                try
                {
                    consumeResult = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    SagaOrchestratorLog.ConsumeFailed(logger, exception.Error.Reason, exception);
                    await Task.Delay(1_000, stoppingToken);
                    continue;
                }

                await DispatchAsync(consumeResult, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            SagaOrchestratorLog.Stopping(logger);
        }
        finally
        {
            consumer.Close();
        }
    }

    private Task DispatchAsync(ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken)
    {
        return consumeResult.Topic switch
        {
            _ when consumeResult.Topic == _options.ReservationRepliedTopic => HandleReservationRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.DecisionRepliedTopic => HandlePaymentDecisionRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.CommitRepliedTopic => HandleCommitRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.ReleaseRepliedTopic => HandleReleaseRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ => Task.CompletedTask
        };
    }

    private async Task HandleReservationRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationReplied>(payload);
        if (reply is null)
        {
            return;
        }

        if (!reply.Reserved)
        {
            if (reply.Backordered)
            {
                // Milestone 74: wait, don't give up. The saga row stays
                // parked at ReserveInventory, so the eventual backorder
                // release reply advances it through TryAdvanceAsync below
                // with no further changes needed. Nothing was charged yet.
                await orderStatusStore.TryTransitionAsync(
                    reply.OrderId, OrderStatuses.Backordered, reply.CorrelationId, cancellationToken);
                await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
                SagaOrchestratorLog.Backordered(logger, reply.OrderId, reply.Sku, reply.CorrelationId);
                return;
            }

            var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.ReserveInventory, cancellationToken);
            if (completed is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
            SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, "RejectedInsufficientStock", latencyMs, completed.CorrelationId);
            await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var advanced = await store.TryAdvanceAsync(reply.OrderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, now, cancellationToken);
        if (advanced is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.DecidePayment, advanced.CorrelationId);

        var request = new PaymentDecisionRequested(
            reply.OrderId,
            advanced.Amount,
            advanced.Currency,
            advanced.CorrelationId,
            now,
            advanced.CustomerId,
            advanced.PaymentMethod,
            advanced.ShippingPostalPrefix);
        await PublishNextStepAsync(_options.DecisionRequestedTopic, reply.OrderId.ToString("N"), request, reply.OrderId, cancellationToken);
    }

    private async Task HandlePaymentDecisionRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<PaymentDecisionReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (reply.Approved)
        {
            var advanced = await store.TryAdvanceAsync(reply.OrderId, SagaStep.DecidePayment, SagaStep.CommitInventory, now, cancellationToken);
            if (advanced is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.CommitInventory, advanced.CorrelationId);
            var request = new InventoryReservationCommitRequested(advanced.ReservationId, reply.OrderId, advanced.Sku, advanced.Quantity, advanced.CorrelationId, now);
            await PublishNextStepAsync(_options.CommitRequestedTopic, advanced.Sku, request, reply.OrderId, cancellationToken);
        }
        else
        {
            // The compensating transaction: undo the step 1 reservation, since payment was the problem, not it.
            var advanced = await store.TryAdvanceAsync(reply.OrderId, SagaStep.DecidePayment, SagaStep.ReleaseInventory, now, cancellationToken);
            if (advanced is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.ReleaseInventory, advanced.CorrelationId);
            var request = new InventoryReservationReleaseRequested(advanced.ReservationId, reply.OrderId, advanced.Sku, advanced.Quantity, advanced.CorrelationId, now);
            await PublishNextStepAsync(_options.ReleaseRequestedTopic, advanced.Sku, request, reply.OrderId, cancellationToken);
        }
    }

    private async Task HandleCommitRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationCommitReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.CommitInventory, cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var outcome = reply.Committed ? "Confirmed" : "ConfirmedButCommitFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);

        // Payment was already approved, so the order is genuinely confirmed
        // either way - a failed inventory commit isn't a reason to stay at Created.
        await orderStatusStore.TryConfirmAsync(reply.OrderId, completed.CorrelationId, cancellationToken);

        if (!reply.Committed)
        {
            // Milestone 69: moves to FulfillmentHold - the customer is owed
            // something, but the stock was never actually deducted, so a
            // human has to resolve it before this can be picked.
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.FulfillmentHold, completed.CorrelationId, cancellationToken);
        }

        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);

        if (reply.Committed)
        {
            await RecordSaleBestEffortAsync(completed.Sku, completed.Quantity, cancellationToken);
        }
    }

    // Analytics side-effect, not a saga step: a failure here (Redis or
    // Catalog unreachable) must never fail or retry the saga completion it
    // reacts to - see BestsellersStore's class comment.
    private async Task RecordSaleBestEffortAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        try
        {
            var product = await catalogClient.FindBySkuAsync(sku, cancellationToken);
            await bestsellersStore.RecordSaleAsync(sku, product?.CategorySlug, quantity, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SagaOrchestratorLog.BestsellerTrackingFailed(logger, sku, exception);
        }
    }

    private async Task HandleReleaseRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationReleaseReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.ReleaseInventory, cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var outcome = reply.Released ? "RejectedPaymentDeclined" : "RejectedPaymentDeclinedButReleaseFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);
        await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
    }

    private async Task PublishNextStepAsync<TRequest>(
        string topic,
        string key,
        TRequest request,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var message = new Message<string, string>
        {
            Key = key,
            Value = JsonSerializer.Serialize(request, SerializerOptions)
        };

        try
        {
            await producer.ProduceAsync(topic, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SagaOrchestratorLog.NextStepPublishFailed(logger, orderId, exception);
        }
    }

    private static T? Deserialize<T>(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
