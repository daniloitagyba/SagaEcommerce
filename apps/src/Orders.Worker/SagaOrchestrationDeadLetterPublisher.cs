using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

public interface ISagaOrchestrationDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

/// <summary>Request-side (OrderCreatedTopic, Avro/byte[]) - see SagaReplyDeadLetterPublisher for the reply side.</summary>
public sealed class SagaOrchestrationDeadLetterPublisher(IProducer<string, string> producer, IOptions<SagaOrchestrationOptions> options)
    : KafkaDeadLetterPublisherBase<byte[]>(producer, options.Value.DeadLetterTopic, "orders_saga.dead_letter.publish", Convert.ToBase64String),
        ISagaOrchestrationDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, byte[]> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
