using System.Globalization;
using global::Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.GetOrder;

namespace Orders.Api.Grpc;

/// <summary>A gRPC transport for the same order read that the REST GetByIdAsync endpoint serves.</summary>
[Authorize(Policy = OrdersAuthorizationPolicies.Read)]
public sealed class OrderQueryGrpcService(GetOrderHandler handler, IConfiguration configuration)
    : OrderQuery.OrderQueryBase
{
    public override async Task<GetOrderResponse> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.Id}' is not a valid order id."));
        }

        GetOrderResult result;
        try
        {
            result = await handler.HandleAsync(id, context.CancellationToken);
        }
        catch (InfrastructureUnavailableException exception)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, exception.Message));
        }

        var httpContext = context.GetHttpContext();

        if (result.Order is null || !httpContext.MayAccess(result.Order.CustomerId))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Order '{id}' was not found."));
        }

        var order = result.Order;

        return new GetOrderResponse
        {
            Id = order.Id.ToString(),
            CustomerId = order.CustomerId,
            AmountCents = (long)Math.Round(order.Amount * 100, MidpointRounding.AwayFromZero),
            Currency = order.Currency,
            Status = order.Status,
            CreatedAt = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = httpContext.GetCorrelationId(),
            InstanceId = configuration["InstanceId"] ?? Environment.MachineName
        };
    }
}
