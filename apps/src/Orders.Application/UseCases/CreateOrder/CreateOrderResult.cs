using Orders.Domain;

namespace Orders.Application.UseCases.CreateOrder;

/// <summary>
/// The live catalog subtotal disagreed with what the caller
/// expected - a product's price moved between when a shopper's cart
/// snapshotted it and when they checked out. Distinct from a validation
/// error: nothing about the request is malformed, the world changed under it.
/// </summary>
public sealed record PriceMismatch(decimal ExpectedSubtotal, decimal ActualSubtotal);

public sealed record IdempotencyConflict(string IdempotencyKey);

public sealed record CreateOrderResult(
    Order? Order,
    Guid EventId,
    IReadOnlyDictionary<string, string[]> ValidationErrors,
    bool WasReplayed = false,
    PriceMismatch? PriceMismatch = null,
    IdempotencyConflict? IdempotencyConflict = null)
{
    public bool IsValid => ValidationErrors.Count == 0
        && PriceMismatch is null
        && IdempotencyConflict is null;
}
