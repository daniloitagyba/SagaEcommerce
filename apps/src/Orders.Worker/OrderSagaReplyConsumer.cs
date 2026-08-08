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
            _options.ReleaseRepliedTopic,
            _options.SettlementRepliedTopic
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

    /// <summary>Public so integration tests can drive it directly, the same shape as OrderSagaOrchestrator.RequestReservationAsync.</summary>
    public Task DispatchAsync(ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken)
    {
        return consumeResult.Topic switch
        {
            _ when consumeResult.Topic == _options.ReservationRepliedTopic => HandleReservationRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.DecisionRepliedTopic => HandlePaymentDecisionRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.CommitRepliedTopic => HandleCommitRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.ReleaseRepliedTopic => HandleReleaseRepliedAsync(consumeResult.Message.Value, cancellationToken),
            _ when consumeResult.Topic == _options.SettlementRepliedTopic => HandleSettlementRepliedAsync(consumeResult.Message.Value, cancellationToken),
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

        if (!reply.Reserved && reply.Backordered)
        {
            // Milestone 74: wait, don't give up - this line stays
            // unanswered (neither Reserved nor rejected) until the
            // eventual backorder-release reply flips it. Milestone 78: the
            // *order* moves to Backordered even if a sibling line already
            // reserved fine - that reservation is held, not released,
            // until every line has an answer.
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.Backordered, reply.CorrelationId, cancellationToken);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            SagaOrchestratorLog.Backordered(logger, reply.OrderId, reply.Sku, reply.CorrelationId);
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Reserved, reply.Reserved, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            // Either an unknown reservation, or the order's saga row is
            // already gone (completed by a sibling line's rejection, or by
            // a timeout) - a redelivered/late reply for it is a no-op.
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (lines.Any(line => line.Reserved == false))
        {
            // Milestone 78's compensation case: at least one line was
            // rejected outright. Release every sibling line that DID
            // reserve successfully before cancelling - the whole order
            // fails together, so a partial reservation left behind would
            // be inventory nothing will ever release.
            var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.ReserveInventory, cancellationToken);
            if (completed is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var line in completed.Lines.Where(line => line.Reserved == true))
            {
                var release = new InventoryReservationReleaseRequested(line.ReservationId, reply.OrderId, line.Sku, line.Quantity, completed.CorrelationId, now);
                await PublishNextStepAsync(_options.ReleaseRequestedTopic, line.Sku, release, reply.OrderId, cancellationToken);
            }

            var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
            SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, "RejectedInsufficientStock", latencyMs, completed.CorrelationId);
            await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            return;
        }

        if (lines.Any(line => line.Reserved is null))
        {
            // Still waiting on at least one more line's reply.
            return;
        }

        var advanceAt = DateTimeOffset.UtcNow;
        var advanced = await store.TryAdvanceAsync(reply.OrderId, SagaStep.ReserveInventory, SagaStep.DecidePayment, advanceAt, cancellationToken);
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
            advanceAt,
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
            foreach (var line in advanced.Lines)
            {
                var request = new InventoryReservationCommitRequested(line.ReservationId, reply.OrderId, line.Sku, line.Quantity, advanced.CorrelationId, now);
                await PublishNextStepAsync(_options.CommitRequestedTopic, line.Sku, request, reply.OrderId, cancellationToken);
            }
        }
        else
        {
            // The compensating transaction: undo the step 1 reservations, since payment was the problem, not them.
            var advanced = await store.TryAdvanceAsync(reply.OrderId, SagaStep.DecidePayment, SagaStep.ReleaseInventory, now, cancellationToken);
            if (advanced is null)
            {
                SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
                return;
            }

            SagaOrchestratorLog.Advanced(logger, reply.OrderId, SagaStep.ReleaseInventory, advanced.CorrelationId);
            foreach (var line in advanced.Lines)
            {
                var request = new InventoryReservationReleaseRequested(line.ReservationId, reply.OrderId, line.Sku, line.Quantity, advanced.CorrelationId, now);
                await PublishNextStepAsync(_options.ReleaseRequestedTopic, line.Sku, request, reply.OrderId, cancellationToken);
            }
        }
    }

    private async Task HandleCommitRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<InventoryReservationCommitReplied>(payload);
        if (reply is null)
        {
            return;
        }

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Committed, reply.Committed, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (lines.Any(line => line.Committed is null))
        {
            // Still waiting on at least one more line's commit reply.
            return;
        }

        var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.CommitInventory, cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var allCommitted = completed.Lines.All(line => line.Committed == true);
        var outcome = allCommitted ? "Confirmed" : "ConfirmedButCommitFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);

        // Payment was already approved, so the order is genuinely confirmed
        // either way - a failed inventory commit isn't a reason to stay at Created.
        await orderStatusStore.TryConfirmAsync(reply.OrderId, completed.CorrelationId, cancellationToken);

        if (!allCommitted)
        {
            // Milestone 69: moves to FulfillmentHold - the customer is owed
            // something, but at least one line's stock was never actually
            // deducted, so a human has to resolve it before this can be picked.
            await orderStatusStore.TryTransitionAsync(
                reply.OrderId, OrderStatuses.FulfillmentHold, completed.CorrelationId, cancellationToken);
        }

        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);

        foreach (var line in completed.Lines.Where(line => line.Committed == true))
        {
            await RecordSaleBestEffortAsync(line.Sku, line.Quantity, cancellationToken);
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

        var lines = await store.RecordLineOutcomeAsync(reply.OrderId, reply.ReservationId, SagaLineOutcomeField.Released, reply.Released, cancellationToken);
        if (lines is null || lines.Count == 0)
        {
            // Also reached for a release reply to a line that Milestone
            // 78's partial-failure compensation already published and
            // completed the order for (HandleReservationRepliedAsync) -
            // that path deletes the saga row immediately rather than
            // waiting at ReleaseInventory, so this is an expected, harmless no-op.
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        if (lines.Any(line => line.Released is null))
        {
            // Still waiting on at least one more line's release reply.
            return;
        }

        var completed = await store.TryCompleteAsync(reply.OrderId, SagaStep.ReleaseInventory, cancellationToken);
        if (completed is null)
        {
            SagaOrchestratorLog.UnknownReply(logger, reply.OrderId);
            return;
        }

        var allReleased = completed.Lines.All(line => line.Released == true);
        var outcome = allReleased ? "RejectedPaymentDeclined" : "RejectedPaymentDeclinedButReleaseFailed";
        var latencyMs = (reply.DecidedAt - completed.RequestedAt).TotalMilliseconds;
        SagaOrchestratorLog.SagaCompleted(logger, reply.OrderId, outcome, latencyMs, completed.CorrelationId);
        await orderStatusStore.TryCancelAsync(reply.OrderId, completed.CorrelationId, cancellationToken);
        await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
    }

    /// <summary>
    /// Milestone 76: not a saga step - the saga row is already gone by the
    /// time an order ships, so there's nothing here to advance or
    /// complete. This is a standalone reconciliation for the one outcome
    /// that must never pass silently: a capture that was supposed to
    /// happen (the order shipped, or a settlement command was in flight)
    /// but the authorization had already expired. Both
    /// PaymentAuthorizationSweeper's bulk expiry and
    /// PaymentSettlementProcessor's settlement-mismatch reply land here
    /// through the same topic, and both mean the same thing: money that
    /// should have moved never did, and it needs a human, not a log line.
    /// </summary>
    private async Task HandleSettlementRepliedAsync(string payload, CancellationToken cancellationToken)
    {
        var reply = Deserialize<PaymentSettlementReplied>(payload);
        if (reply is null || reply.State != PaymentStates.Expired)
        {
            return;
        }

        var moved = await orderStatusStore.TryTransitionAsync(
            reply.OrderId, OrderStatuses.FulfillmentHold, reply.CorrelationId, cancellationToken);

        if (moved == StatusTransitionResult.Transitioned)
        {
            await cacheInvalidator.InvalidateAsync(reply.OrderId, cancellationToken);
            SagaOrchestratorLog.SettlementReconciled(logger, reply.OrderId, reply.State, reply.CorrelationId);
        }
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
