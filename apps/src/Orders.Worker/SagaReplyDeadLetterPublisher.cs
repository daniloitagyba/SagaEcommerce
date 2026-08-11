using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public interface ISagaReplyDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

/// <summary>Reply-side (the five *Replied topics, JSON/string) - see SagaOrchestrationDeadLetterPublisher for the request side.</summary>
public sealed class SagaReplyDeadLetterPublisher(IProducer<string, string> producer, IOptions<SagaOrchestrationOptions> options)
    : KafkaDeadLetterPublisherBase<string>(producer, options.Value.DeadLetterTopic, "orders_saga_reply.dead_letter.publish", value => value ?? string.Empty),
        ISagaReplyDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
