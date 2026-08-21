namespace Cart.Service.Domain;

public sealed record CartSnapshot(
    string CartId,
    IReadOnlyList<CartLineItem> Items,
    TimeSpan? TimeToLive,
    long Version,
    CartCrdtState State);
