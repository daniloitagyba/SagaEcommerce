using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avro.Generic;
using BuildingBlocks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;
using Polly.Registry;

namespace Orders.Infrastructure.Messaging;

public interface IOrderEventPublisher
{
    Task PublishAsync(OrderCreated orderCreated, CancellationToken cancellationToken);

    Task PublishAsync(OrderStatusChanged statusChanged, CancellationToken cancellationToken);
}

public sealed class KafkaOrderEventPublisher(
    IProducer<string, byte[]> producer,
    ISchemaRegistryClient schemaRegistryClient,
    IOptions<KafkaOptions> options,
    ResiliencePipelineProvider<string> pipelineProvider) : IOrderEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Polly.ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);
    private readonly AvroSerializer<GenericRecord> _avroSerializer =
        new(schemaRegistryClient, new AvroSerializerConfig { AutoRegisterSchemas = true });

    public async Task PublishAsync(OrderCreated orderCreated, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, orderCreated.CorrelationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var record = OrderCreatedAvroSchema.ToGenericRecord(orderCreated);
        var context = new SerializationContext(MessageComponentType.Value, options.Value.OrderCreatedTopic, headers);
        var value = await _avroSerializer.SerializeAsync(record, context);

        var message = new Message<string, byte[]>
        {
            Key = orderCreated.OrderId.ToString("N"),
            Value = value,
            Headers = headers
        };

        await _pipeline.ExecuteAsync(
            async ct => await producer.ProduceAsync(options.Value.OrderCreatedTopic, message, ct).WaitAsync(ct),
            cancellationToken);
    }

    /// <summary>Publishes as plain JSON, not Avro, since this is an internal signal only OrderProjectionProcessor reads, with no external schema registry consumer to justify Avro.</summary>
    public async Task PublishAsync(OrderStatusChanged statusChanged, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, statusChanged.CorrelationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var message = new Message<string, byte[]>
        {
            Key = statusChanged.OrderId.ToString("N"),
            Value = JsonSerializer.SerializeToUtf8Bytes(statusChanged, SerializerOptions),
            Headers = headers
        };

        await _pipeline.ExecuteAsync(
            async ct => await producer.ProduceAsync(options.Value.OrderStatusChangedTopic, message, ct).WaitAsync(ct),
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
