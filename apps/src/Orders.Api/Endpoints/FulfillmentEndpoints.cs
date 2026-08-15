using BuildingBlocks;
using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.AdvanceFulfillment;

namespace Orders.Api.Endpoints;

public sealed record AdvanceFulfillmentRequest(string? Status);

/// <summary>
/// The warehouse's way in. Fulfilment is driven by an
/// external actor - a picker, a carrier webhook, an ops user - so it gets
/// an explicit endpoint rather than a timer pretending orders ship
/// themselves; the interesting part is that an illegal move is refused by
/// the same compare-and-set that performs a legal one.
///
/// Admin-gated, not Write - this is the warehouse's endpoint,
/// able to move <em>any</em> customer's order and reach every fulfilment
/// state including Picking and Shipped, neither of which a shopper should
/// ever trigger themselves. A shopper cancelling their own order has its
/// own, narrower route - see CancellationEndpoints. Restricted to
/// <see cref="OrderStatuses.FulfillmentDrivableTargets"/>, not the full
/// <see cref="OrderStatuses.TransitionableTargets"/> table - Confirmed,
/// Backordered and Returned are legal moves, but only the saga or the
/// return flow, not an operator's direct status flip, is allowed to make them.
/// </summary>
public static class FulfillmentEndpoints
{
    public static IEndpointRouteBuilder MapFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{id:guid}/fulfillment", AdvanceAsync)
            .WithTags("Fulfillment")
            .RequireAuthorization(OrdersAuthorizationPolicies.Admin);

        return endpoints;
    }

    private static async Task<IResult> AdvanceAsync(
        Guid id,
        AdvanceFulfillmentRequest request,
        AdvanceFulfillmentHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["status"] = ["status is required."]
            });
        }

        var normalizedStatus = request.Status.Trim();

        // A status can be legal in OrderStatuses' table (TransitionableTargets)
        // without this endpoint being the one allowed to set it directly -
        // Confirmed, Backordered and Returned are each owned by an
        // aggregate or the saga that keeps inventory/payment/refund state
        // consistent with the status flip; a direct write here would skip
        // all of it. Checked before IllegalTransition below so the two
        // refusals stay distinguishable: "not a real status" vs "a real
        // status, but not this endpoint's to set".
        if (!OrderStatuses.FulfillmentDrivableTargets.Contains(normalizedStatus, StringComparer.Ordinal)
            && OrderStatuses.TransitionableTargets.Contains(normalizedStatus, StringComparer.Ordinal))
        {
            return Results.Problem(
                detail: $"'{normalizedStatus}' is reached automatically (by the saga or the return flow), not set directly through fulfilment. This endpoint may only set: {string.Join(", ", OrderStatuses.FulfillmentDrivableTargets)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Illegal Transition");
        }

        AdvanceFulfillmentResult result;
        try
        {
            result = await handler.HandleAsync(id, request.Status, httpContext.GetCorrelationId(), cancellationToken);
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

            // 422, not 400: well-formed, but not reachable from here - listing valid targets makes the refusal actionable.
            AdvanceFulfillmentOutcome.IllegalTransition => Results.Problem(
                detail: $"'{request.Status}' is not a status an order can be moved into. Valid targets: {string.Join(", ", OrderStatuses.TransitionableTargets)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Illegal Transition"),

            // 409: legal in general, but this order isn't in a state it can be made from.
            AdvanceFulfillmentOutcome.NotApplicable => Results.Problem(
                detail: $"This order cannot move to '{request.Status}' from its current state. It must first be in one of: {string.Join(", ", OrderStatuses.PredecessorsOf(request.Status))}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Transition Not Applicable"),

            _ => Results.NotFound()
        };
    }
}
