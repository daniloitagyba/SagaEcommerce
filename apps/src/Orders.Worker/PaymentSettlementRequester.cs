using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildingBlocks;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Orders.Worker;

/// <summary>
/// Milestone 68: asks Payments to finish what the authorization started -
/// capture the money at Shipped (Milestone 69), or release the hold on
/// cancellation.
///
/// <para>
/// Fire-and-forget by design: a publish failure is logged, not propagated.
/// The order has already transitioned, and the authorization expiry
/// sweeper on the Payments side is the backstop, so a lost capture command
/// degrades to "the customer was not charged", never to money held forever.
/// </para>
/// </summary>
public sealed class PaymentSettlementRequester(
    IProducer<string, string> producer,
    IOptions<PaymentSettlementRequestOptions> options,
    ILogger<PaymentSettlementRequester> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PaymentSettlementRequestOptions _options = options.Value;

    public Task RequestCaptureAsync(Guid orderId, string correlationId, CancellationToken cancellationToken)
    {
        var request = new PaymentCaptureRequested(orderId, correlationId, DateTimeOffset.UtcNow);
        return PublishAsync(_options.CaptureRequestedTopic, orderId, correlationId, request, cancellationToken);
    }

    public Task RequestVoidAsync(Guid orderId, string correlationId, string reason, CancellationToken cancellationToken)
    {
        var request = new PaymentVoidRequested(orderId, reason, correlationId, DateTimeOffset.UtcNow);
        return PublishAsync(_options.VoidRequestedTopic, orderId, correlationId, request, cancellationToken);
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

        try
        {
            await producer.ProduceAsync(topic, message, cancellationToken);
            SettlementRequestLog.Requested(logger, topic, orderId, correlationId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettlementRequestLog.PublishFailed(logger, topic, orderId, exception);
        }
    }

    private static void AddHeader(Headers headers, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers.Add(key, Encoding.UTF8.GetBytes(value));
        }
    }
}

public sealed class PaymentSettlementRequestOptions
{
    public const string SectionName = "PaymentSettlementRequest";

    public string CaptureRequestedTopic { get; init; } = "payments.capture-requested.v1";

    public string VoidRequestedTopic { get; init; } = "payments.void-requested.v1";
}

public sealed partial class SettlementRequestLog
{
    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Requested payment settlement on {Topic} for order {OrderId} with correlation {CorrelationId}")]
    public static partial void Requested(ILogger logger, string topic, Guid orderId, string correlationId);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Error, Message = "Failed to publish a settlement request on {Topic} for order {OrderId} - the authorization expiry sweeper is the backstop")]
    public static partial void PublishFailed(ILogger logger, string topic, Guid orderId, Exception exception);
}
