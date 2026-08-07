using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Inventory.Service;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace Inventory.Service.Messaging;

public interface IInventoryEventPublisher
{
    Task PublishAsync(InventoryReservationReplied reply, CancellationToken cancellationToken);

    Task PublishAsync(InventoryReservationCommitReplied reply, CancellationToken cancellationToken);

    Task PublishAsync(InventoryReservationReleaseReplied reply, CancellationToken cancellationToken);

    Task PublishAsync(InventoryRestockReplied reply, CancellationToken cancellationToken);

    Task PublishAsync(WarehouseReplenishmentNeeded signal, CancellationToken cancellationToken);
}

public sealed class KafkaInventoryEventPublisher(
    IProducer<string, string> producer,
    IOptions<InventoryKafkaOptions> options,
    ResiliencePipelineProvider<string> pipelineProvider) : IInventoryEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Polly.ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);

    public Task PublishAsync(InventoryReservationReplied reply, CancellationToken cancellationToken)
    {
        return PublishInternalAsync(options.Value.ReservationRepliedTopic, reply.OrderId, reply.CorrelationId, reply, cancellationToken);
    }

    public Task PublishAsync(InventoryReservationCommitReplied reply, CancellationToken cancellationToken)
    {
        return PublishInternalAsync(options.Value.CommitRepliedTopic, reply.OrderId, reply.CorrelationId, reply, cancellationToken);
    }

    public Task PublishAsync(InventoryReservationReleaseReplied reply, CancellationToken cancellationToken)
    {
        return PublishInternalAsync(options.Value.ReleaseRepliedTopic, reply.OrderId, reply.CorrelationId, reply, cancellationToken);
    }

    public Task PublishAsync(InventoryRestockReplied reply, CancellationToken cancellationToken)
    {
        return PublishInternalAsync(options.Value.RestockRepliedTopic, reply.OrderId, reply.CorrelationId, reply, cancellationToken);
    }

    /// <summary>
    /// Milestone 73: keyed by SKU rather than an order id, because this
    /// event is about a shelf, not a purchase - and per-SKU ordering is the
    /// guarantee this service already relies on everywhere else.
    /// </summary>
    public Task PublishAsync(WarehouseReplenishmentNeeded signal, CancellationToken cancellationToken)
    {
        return PublishInternalAsync(
            options.Value.ReplenishmentNeededTopic,
            signal.Sku,
            signal.CorrelationId,
            signal,
            cancellationToken);
    }

    private Task PublishInternalAsync<TReply>(
        string topic,
        Guid orderId,
        string correlationId,
        TReply reply,
        CancellationToken cancellationToken) =>
        PublishInternalAsync(topic, orderId.ToString("N"), correlationId, reply, cancellationToken);

    private async Task PublishInternalAsync<TReply>(
        string topic,
        string partitionKey,
        string correlationId,
        TReply reply,
        CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, correlationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var message = new Message<string, string>
        {
            Key = partitionKey,
            Value = JsonSerializer.Serialize(reply, SerializerOptions),
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
