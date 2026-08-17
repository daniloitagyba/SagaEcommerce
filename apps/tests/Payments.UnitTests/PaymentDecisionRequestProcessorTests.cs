using System.Text.Json;
using BuildingBlocks;
using Payments.Service;

namespace Payments.UnitTests;

/// <summary>PaymentDecisionRequestProcessor needs a live PaymentsDbContext and PaymentRiskEvaluator so it isn't unit-testable end to end, but DeserializeAndValidate has no dependency on either and had no coverage despite being the first thing every decision request passes through.</summary>
public sealed class PaymentDecisionRequestProcessorTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static string Serialize(PaymentDecisionRequested request) =>
        JsonSerializer.Serialize(request, SerializerOptions);

    [Fact]
    public void AWellFormedRequestRoundTrips()
    {
        var request = new PaymentDecisionRequested(
            OrderId: Guid.NewGuid(),
            Amount: 42.50m,
            Currency: "BRL",
            CorrelationId: "correlation-1",
            RequestedAt: DateTimeOffset.UtcNow,
            CustomerId: "customer-1",
            PaymentMethod: PaymentMethods.Pix,
            ShippingPostalPrefix: "01");

        var result = PaymentDecisionRequestProcessor.DeserializeAndValidate(Serialize(request));

        Assert.Equal(request.OrderId, result.OrderId);
        Assert.Equal(request.CustomerId, result.CustomerId);
        Assert.Equal(request.Amount, result.Amount);
    }

    [Fact]
    public void MalformedJsonIsRejected()
    {
        var exception = Assert.Throws<InvalidPaymentDecisionRequestException>(
            () => PaymentDecisionRequestProcessor.DeserializeAndValidate("not json"));

        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public void AnEmptyOrderIdIsRejected()
    {
        var request = new PaymentDecisionRequested(
            OrderId: Guid.Empty,
            Amount: 42.50m,
            Currency: "BRL",
            CorrelationId: "correlation-1",
            RequestedAt: DateTimeOffset.UtcNow,
            CustomerId: "customer-1",
            PaymentMethod: PaymentMethods.Pix,
            ShippingPostalPrefix: "01");

        Assert.Throws<InvalidPaymentDecisionRequestException>(
            () => PaymentDecisionRequestProcessor.DeserializeAndValidate(Serialize(request)));
    }
}
