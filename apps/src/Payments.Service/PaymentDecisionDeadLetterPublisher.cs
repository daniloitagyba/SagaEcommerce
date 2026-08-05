using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Payments.Service;

public interface IPaymentDecisionDeadLetterPublisher
{
    Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken);
}

public sealed class PaymentDecisionDeadLetterPublisher(IProducer<string, string> producer, IOptions<PaymentDecisionRequestOptions> options)
    : KafkaDeadLetterPublisherBase<string>(producer, options.Value.DeadLetterTopic, "payments.decision_request.dead_letter.publish", payload => payload),
        IPaymentDecisionDeadLetterPublisher
{
    public Task PublishAsync(
        ConsumeResult<string, string> consumeResult,
        Exception exception,
        int attemptCount,
        CancellationToken cancellationToken)
        => PublishCoreAsync(consumeResult, exception, attemptCount, cancellationToken);
}
