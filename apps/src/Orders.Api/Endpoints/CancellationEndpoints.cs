using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.AdvanceFulfillment;

namespace Orders.Api.Endpoints;

/// <summary>
/// Milestone 83: the shopper's own way to cancel an order - the single
/// most common e-commerce self-service action, and one this lab had no
/// route to at all before this milestone (the only way to reach Cancelled
/// was the warehouse's POST .../fulfillment endpoint, now Admin-gated).
/// Reuses AdvanceFulfillmentHandler's compare-and-set machinery through a
/// narrower, ownership-checked entry point rather than duplicating it.
/// </summary>
public static class CancellationEndpoints
{
    public static IEndpointRouteBuilder MapCancellationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{id:guid}/cancellation", CancelAsync)
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
        AdvanceFulfillmentResult result;
        try
        {
            result = await handler.HandleSelfServiceCancelAsync(
                id, httpContext.GetCallerIdentity(), httpContext.GetCorrelationId(), cancellationToken);
        }
        catch (InfrastructureUnavailableException)
        {
            httpContext.Response.Headers["Retry-After"] = "5";
            return Results.Problem(
                detail: "PostgreSQL is currently unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Service Unavailable");
        }

        return result.Outcome switch
        {
            AdvanceFulfillmentOutcome.Advanced => Results.Ok(new
            {
                orderId = id,
                status = result.Status,
                correlationId = httpContext.GetCorrelationId()
            }),

            // 409: an order that has already started being picked, or was
            // never this caller's to begin with, needs the warehouse
            // (or nobody) - never a 422, since a shopper cancelling their
            // own order is always a legal request in principle.
            AdvanceFulfillmentOutcome.NotApplicable => Results.Problem(
                detail: "This order can no longer be cancelled - it may already be in fulfilment, or already resolved. Contact support if it needs to be stopped.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Cancellation Not Applicable"),

            _ => Results.NotFound()
        };
    }
}
