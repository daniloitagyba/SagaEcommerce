using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.UseCases.AdvanceFulfillment;

namespace Orders.Api.Endpoints;

/// <summary>The shopper's self-service endpoint for cancelling an order.</summary>
public static class CancellationEndpoints
{
    public static IEndpointRouteBuilder MapCancellationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/{id:guid}/cancellation", CancelAsync)
            .WithTags("Orders")
            .RequireAuthorization(OrdersAuthorizationPolicies.Write);

        return endpoints;
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        AdvanceFulfillmentHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleSelfServiceCancelAsync(
            id, httpContext.GetCallerIdentity(), httpContext.GetCorrelationId(), cancellationToken);

        return result.Outcome switch
        {
            AdvanceFulfillmentOutcome.Advanced => Results.Ok(new
            {
                orderId = id,
                status = result.Status,
                correlationId = httpContext.GetCorrelationId()
            }),

            AdvanceFulfillmentOutcome.NotApplicable => Results.Problem(
                detail: "This order can no longer be cancelled - it may already be in fulfilment, or already resolved. Contact support if it needs to be stopped.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Cancellation Not Applicable"),

            _ => Results.NotFound()
        };
    }
}
