namespace Orders.Domain;

/// <summary>Where the order is going; enough to decide shipping cost and tax jurisdiction, deliberately no more.</summary>
public sealed record ShippingAddress(
    string Line1,
    string City,
    string Region,
    string PostalCode)
{
    /// <summary>The first two digits of the Brazilian CEP, which determines the shipping zone; derived, never stored.</summary>
    public string PostalPrefix => new string([.. PostalCode.Where(char.IsDigit)]) is { Length: >= 2 } digits
        ? digits[..2]
        : string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Line1)
        && !string.IsNullOrWhiteSpace(City)
        && !string.IsNullOrWhiteSpace(Region)
        && PostalPrefix.Length == 2;
}
