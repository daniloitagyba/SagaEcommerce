using Orders.Domain;

namespace Orders.Application.UseCases.CreateOrder;

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
