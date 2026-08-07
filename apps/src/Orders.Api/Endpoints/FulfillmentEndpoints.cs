using BuildingBlocks;
using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.Exceptions;
using Orders.Application.UseCases.AdvanceFulfillment;

namespace Orders.Api.Endpoints;

public sealed record AdvanceFulfillmentRequest(string? Status);

/// <summary>
/// Milestone 69: the warehouse's way in. Fulfilment is driven by an
/// external actor - a picker, a carrier webhook, an ops user - so it gets
/// an explicit endpoint rather than a timer pretending orders ship
/// themselves; the interesting part is that an illegal move is refused by
/// the same compare-and-set that performs a legal one.
/// </summary>
public static class FulfillmentEndpoints
{
    public static IEndpointRouteBuilder MapFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/orders/{id:guid}/fulfillment", AdvanceAsync)
            .WithTags("Fulfillment")
            .RequireAuthorization(OrdersAuthorizationPolicies.Write);

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
