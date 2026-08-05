using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payments.Service;
using Polly.Registry;

namespace Payments.Service.Messaging;

public interface IPaymentDecisionReplyPublisher
{
    Task PublishAsync(PaymentDecisionReplied reply, CancellationToken cancellationToken);
}

public sealed class KafkaPaymentDecisionReplyPublisher(
    IProducer<string, string> producer,
    IOptions<PaymentDecisionRequestOptions> options,
    ResiliencePipelineProvider<string> pipelineProvider) : IPaymentDecisionReplyPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly Polly.ResiliencePipeline _pipeline = pipelineProvider.GetPipeline(ResilienceExtensions.KafkaProducerPipeline);

    public async Task PublishAsync(PaymentDecisionReplied reply, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        AddHeader(headers, MessagingHeaders.CorrelationId, reply.CorrelationId);
        AddHeader(headers, MessagingHeaders.TraceParent, Activity.Current?.Id);
        AddHeader(headers, MessagingHeaders.TraceState, Activity.Current?.TraceStateString);

        var message = new Message<string, string>
        {
            Key = reply.OrderId.ToString("N"),
            Value = JsonSerializer.Serialize(reply, SerializerOptions),
            Headers = headers
        };

        await _pipeline.ExecuteAsync(
            async ct => await producer.ProduceAsync(options.Value.DecisionRepliedTopic, message, ct).WaitAsync(ct),
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
