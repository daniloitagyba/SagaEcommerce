using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace Orders.Infrastructure.Messaging;

public interface IPaymentSettlementCommandPublisher
{
    Task PublishCaptureAsync(PaymentCaptureRequested request, CancellationToken cancellationToken);

    /// <summary>Replaces PublishVoidAsync - see PaymentCancellationRequested.</summary>
    Task PublishCancellationAsync(PaymentCancellationRequested request, CancellationToken cancellationToken);

    Task PublishRefundAsync(PaymentRefundRequested request, CancellationToken cancellationToken);

    Task PublishRestockAsync(InventoryRestockRequested request, CancellationToken cancellationToken);

    /// <summary>A backordered order was cancelled - stop waiting for stock on its behalf.</summary>
    Task PublishBackorderCancellationAsync(BackorderCancellationRequested request, CancellationToken cancellationToken);
}

/// <summary>Publishes settlement commands the fulfilment API queued on the outbox, draining already-committed rows (unlike Orders.Worker's PaymentSettlementRequester, which produces inline).</summary>
public sealed class KafkaPaymentSettlementCommandPublisher(
    IProducer<string, string> producer,
    IOptions<PaymentSettlementCommandOptions> options,
    ResiliencePipelineProvider<string> pipelineProvider) : IPaymentSettlementCommandPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Polly.ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);

    public Task PublishCaptureAsync(PaymentCaptureRequested request, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.CaptureRequestedTopic, request.OrderId, request.CorrelationId, request, cancellationToken);

    public Task PublishCancellationAsync(PaymentCancellationRequested request, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.CancellationRequestedTopic, request.OrderId, request.CorrelationId, request, cancellationToken);

    public Task PublishRefundAsync(PaymentRefundRequested request, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.RefundRequestedTopic, request.OrderId, request.CorrelationId, request, cancellationToken);

    /// <summary>Keyed by OrderId, not Sku - unlike a restock, cancelling backordered lines is not per-SKU serialised in Inventory.</summary>
    public Task PublishBackorderCancellationAsync(BackorderCancellationRequested request, CancellationToken cancellationToken) =>
        PublishAsync(options.Value.BackorderCancellationRequestedTopic, request.OrderId, request.CorrelationId, request, cancellationToken);

    /// <summary>Keyed by SKU, not order id, so the restock lands on the same partition as the reservation it reverses.</summary>
    public async Task PublishRestockAsync(InventoryRestockRequested request, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, request.CorrelationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var message = new Message<string, string>
        {
            Key = request.Sku,
            Value = JsonSerializer.Serialize(request, SerializerOptions),
            Headers = headers
        };

        await _pipeline.ExecuteAsync(
            async ct => await producer.ProduceAsync(options.Value.RestockRequestedTopic, message, ct).WaitAsync(ct),
            cancellationToken);
    }

    private async Task PublishAsync<TRequest>(
        string topic,
        Guid orderId,
        string correlationId,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, correlationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var message = new Message<string, string>
        {
            Key = orderId.ToString("N"),
            Value = JsonSerializer.Serialize(request, SerializerOptions),
            Headers = headers
        };

        await _pipeline.ExecuteAsync(
            async ct => await producer.ProduceAsync(topic, message, ct).WaitAsync(ct),
            cancellationToken);
    }

    private static void AddHeader(Headers headers, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.Add(key, Encoding.UTF8.GetBytes(value));
        }
    }
}

public sealed class PaymentSettlementCommandOptions
{
    public const string SectionName = "PaymentSettlementRequest";

    public string CaptureRequestedTopic { get; init; } = "payments.capture-requested.v1";

    public string CancellationRequestedTopic { get; init; } = "payments.cancellation-requested.v1";

    public string RefundRequestedTopic { get; init; } = "payments.refund-requested.v1";

    public string RestockRequestedTopic { get; init; } = "inventory.restock-requested.v1";

    public string BackorderCancellationRequestedTopic { get; init; } = "inventory.backorder-cancellation-requested.v1";
}
