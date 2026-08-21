using System.Diagnostics;
using System.Text.Json;
using BuildingBlocks;
using Microsoft.Extensions.Logging;
using Orders.Application.Ports;
using Orders.Domain;

namespace Orders.Application.UseCases.CreateOrder;

public sealed class CreateOrderHandler(
    IOrderCreationRepository repository,
    OrderPricingService pricingService,
    TimeProvider timeProvider,
    ILogger<CreateOrderHandler> logger,
    IOrderMetrics? metrics = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<CreateOrderResult> HandleAsync(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var errors = CreateOrderCommandValidator.Validate(command);
        if (errors.Count > 0)
        {
            return new CreateOrderResult(null, Guid.Empty, errors);
        }

        var customerId = CreateOrderCommandValidator.NormalizeCustomerId(command.CustomerId!);
        var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? null
            : command.IdempotencyKey;
        var requestHash = idempotencyKey is null
            ? null
            : OrderRequestHasher.Compute(command, customerId);

        if (idempotencyKey is not null)
        {
            var existing = await repository.FindIdempotencyAsync(customerId, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return await ResolveExistingAsync(
                    command, idempotencyKey, requestHash!, existing, errors, cancellationToken);
            }
        }

        PricedCheckout? checkout = null;
        if (command.IsLineItemCheckout)
        {
            (checkout, var pricingErrors) = await pricingService.PriceAsync(command, cancellationToken);
            if (pricingErrors.Count > 0)
            {
                return new CreateOrderResult(null, Guid.Empty, pricingErrors);
            }

            if (command.ExpectedSubtotal is { } expectedSubtotal
                && expectedSubtotal != checkout!.Breakdown.Subtotal.Amount)
            {
                return new CreateOrderResult(
                    null,
                    Guid.Empty,
                    errors,
                    PriceMismatch: new PriceMismatch(expectedSubtotal, checkout.Breakdown.Subtotal.Amount));
            }
        }

        return await CreateAndPersistAsync(
            command, customerId, checkout, idempotencyKey, requestHash, errors, cancellationToken);
    }

    private async Task<CreateOrderResult> CreateAndPersistAsync(
        CreateOrderCommand command,
        string customerId,
        PricedCheckout? checkout,
        string? idempotencyKey,
        string? requestHash,
        IReadOnlyDictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var createdAt = timeProvider.GetUtcNow();

        var order = checkout is null
            ? Order.Create(
                customerId,
                command.Amount,
                CreateOrderCommandValidator.NormalizeCurrency(command.Currency!),
                createdAt)
            : Order.CreateWithLines(
                customerId,
                checkout.Currency,
                createdAt,
                command.CouponCode?.Trim().ToUpperInvariant(),
                checkout.Lines,
                checkout.Breakdown.DiscountTotal.Amount,
                checkout.Breakdown.ShippingTotal.Amount,
                checkout.Breakdown.TaxTotal.Amount,
                command.PaymentMethod ?? PaymentMethods.Pix,
                command.ShippingAddress,
                checkout.CampaignCode);

        var orderCreated = new OrderCreated(
            Guid.NewGuid(),
            order.Id,
            order.CustomerId,
            order.Amount,
            order.Currency,
            order.CreatedAt,
            command.CorrelationId,
            [.. order.Lines.Select(line => new OrderCreatedLine(
                line.Sku,
                line.ProductName,
                line.Quantity,
                line.UnitPrice,
                line.LineTotal))],
            PaymentMethod: order.PaymentMethod,
            ShippingPostalPrefix: order.ShippingAddress?.PostalPrefix ?? string.Empty);
        var outboxMessage = OutboxMessage.Create(
            orderCreated.EventId,
            nameof(OrderCreated),
            JsonSerializer.Serialize(orderCreated, SerializerOptions),
            orderCreated.OccurredAt,
            command.CorrelationId,
            Activity.Current?.Id,
            Activity.Current?.TraceStateString);

        Activity.Current?.SetTag("order.id", order.Id);
        Activity.Current?.SetTag("order.currency", order.Currency);
        Activity.Current?.SetTag("messaging.message.id", orderCreated.EventId);
        Activity.Current?.SetTag("service.instance.id", command.InstanceId);

        var couponReservation = checkout?.CouponCode is { } couponCode
            ? new CouponReservation(couponCode, order.Id, order.CustomerId, createdAt)
            : null;

        var campaignReservation = checkout?.CampaignCode is { } campaignCode
            ? new CampaignReservation(campaignCode, checkout.CampaignAmount, order.Id, createdAt)
            : null;

        var idempotencyClaim = idempotencyKey is null
            ? null
            : new OrderIdempotencyClaim(
                customerId, idempotencyKey, requestHash!, order.Id, createdAt);
        var writeResult = await repository.AddAsync(
            order, outboxMessage, couponReservation, idempotencyClaim, cancellationToken, campaignReservation);

        if (writeResult.Outcome != OrderWriteOutcome.Created)
        {
            return await ResolveExistingAsync(
                command,
                idempotencyKey!,
                requestHash!,
                new OrderIdempotencyEntry(writeResult.OrderId, writeResult.ExistingRequestHash!),
                errors,
                cancellationToken);
        }

        (metrics ?? NullOrderMetrics.Instance).RecordCreated(order.Currency);
        CreateOrderLog.OrderAccepted(
            logger,
            order.Id,
            orderCreated.EventId,
            command.InstanceId,
            command.CorrelationId);

        return new CreateOrderResult(order, orderCreated.EventId, errors);
    }

    private async Task<CreateOrderResult> ResolveExistingAsync(
        CreateOrderCommand command,
        string idempotencyKey,
        string requestHash,
        OrderIdempotencyEntry existing,
        IReadOnlyDictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            CreateOrderLog.IdempotencyConflict(logger, idempotencyKey, command.CorrelationId);
            return new CreateOrderResult(
                null,
                Guid.Empty,
                errors,
                IdempotencyConflict: new IdempotencyConflict(idempotencyKey));
        }

        var order = await repository.FindByIdAsync(existing.OrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Idempotency key '{idempotencyKey}' references missing order '{existing.OrderId}'.");
        CreateOrderLog.IdempotentReplay(logger, order.Id, idempotencyKey, command.CorrelationId);
        return new CreateOrderResult(order, Guid.Empty, errors, WasReplayed: true);
    }
}

public sealed partial class CreateOrderLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Stored order {OrderId} and queued outbox event {EventId} from instance {InstanceId} with correlation {CorrelationId}")]
    public static partial void OrderAccepted(ILogger logger, Guid orderId, Guid eventId, string instanceId, string correlationId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Information,
        Message = "Replayed idempotent create for order {OrderId} using Idempotency-Key {IdempotencyKey} with correlation {CorrelationId}")]
    public static partial void IdempotentReplay(ILogger logger, Guid orderId, string idempotencyKey, string correlationId);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Rejected reuse of Idempotency-Key {IdempotencyKey} with a different request payload (correlation {CorrelationId})")]
    public static partial void IdempotencyConflict(ILogger logger, string idempotencyKey, string correlationId);
}
