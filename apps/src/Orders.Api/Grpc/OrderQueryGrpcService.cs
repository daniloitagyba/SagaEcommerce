using System.Globalization;
using global::Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.UseCases.GetOrder;

namespace Orders.Api.Grpc;

/// <summary>
/// A gRPC transport for the same read GetByIdAsync (REST)
/// already serves - same handler, same cache, same database, no
/// duplicated business logic. Exists to demonstrate a real, concrete
/// consequence of HTTP/2 for the mesh: Linkerd load-balances gRPC calls
/// per request, not per connection the way it does for a REST client's
/// long-lived HTTP/1.1 keep-alive connection.
/// </summary>
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

        var result = await handler.HandleAsync(id, context.CancellationToken);
        var httpContext = context.GetHttpContext();

        // Same 404-hides-ownership reasoning as the REST
        // GetByIdAsync this service mirrors - a gRPC transport for the same
        // read is still the same read, and inherits the same gap otherwise.
        if (result.Order is null || !httpContext.MayAccess(result.Order.CustomerId))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Order '{id}' was not found."));
        }

        var order = result.Order;

        return new GetOrderResponse
        {
            Id = order.Id.ToString(),
            CustomerId = order.CustomerId,
            Amount = (double)order.Amount,
            Currency = order.Currency,
            Status = order.Status,
            CreatedAt = order.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            CorrelationId = httpContext.GetCorrelationId(),
            InstanceId = configuration["InstanceId"] ?? Environment.MachineName
        };
    }
}
