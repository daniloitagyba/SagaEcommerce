namespace Cart.Service.Data;

public sealed class CartOptions
{
    public const string SectionName = "Cart";

    /// <summary>Sliding TTL applied to the whole cart on every write.</summary>
    public int TimeToLiveSeconds { get; init; } = 1_800;
}
