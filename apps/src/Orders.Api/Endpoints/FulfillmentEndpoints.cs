using BuildingBlocks;
using Orders.Api.Authorization;
using Orders.Api.Middleware;
using Orders.Application.UseCases.AdvanceFulfillment;

namespace Orders.Api.Endpoints;

public sealed record AdvanceFulfillmentRequest(string? Status);

/// <summary>The warehouse's endpoint for driving an order through fulfilment states.</summary>
public static class FulfillmentEndpoints
{
    public static IEndpointRouteBuilder MapFulfillmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/{id:guid}/fulfillment", AdvanceAsync)
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

        if (!OrderStatuses.FulfillmentDrivableTargets.Contains(normalizedStatus, StringComparer.Ordinal)
            && OrderStatuses.TransitionableTargets.Contains(normalizedStatus, StringComparer.Ordinal))
        {
            return Results.Problem(
                detail: $"'{normalizedStatus}' is reached automatically (by the saga or the return flow), not set directly through fulfilment. This endpoint may only set: {string.Join(", ", OrderStatuses.FulfillmentDrivableTargets)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Illegal Transition");
        }

        var result = await handler.HandleAsync(id, request.Status, httpContext.GetCorrelationId(), cancellationToken);

        return result.Outcome switch
        {
            AdvanceFulfillmentOutcome.Advanced => Results.Ok(new
            {
                orderId = id,
                status = result.Status,
                correlationId = httpContext.GetCorrelationId()
            }),

            AdvanceFulfillmentOutcome.IllegalTransition => Results.Problem(
                detail: $"'{request.Status}' is not a status an order can be moved into. Valid targets: {string.Join(", ", OrderStatuses.TransitionableTargets)}.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Illegal Transition"),

            AdvanceFulfillmentOutcome.NotApplicable => Results.Problem(
                detail: $"This order cannot move to '{request.Status}' from its current state. It must first be in one of: {string.Join(", ", OrderStatuses.PredecessorsOf(request.Status))}.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Transition Not Applicable"),

            _ => Results.NotFound()
        };
    }
}
