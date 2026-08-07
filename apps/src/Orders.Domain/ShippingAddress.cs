namespace Orders.Domain;

/// <summary>
/// Milestone 71: where the order is going.
///
/// Enough of an address to decide the two things that actually depend on
/// geography in this lab - what shipping costs and what tax applies - and
/// deliberately no more. Street-level correctness needs address validation
/// against a postal database, which is a whole product rather than a
/// distributed-systems concern.
/// </summary>
public sealed record ShippingAddress(
    string Line1,
    string City,
    string Region,
    string PostalCode)
{
    /// <summary>
    /// The first two digits of the Brazilian CEP, which is what actually
    /// determines the shipping zone. Kept as a derived property rather than
    /// a stored one so an address can never disagree with its own zone key.
    /// </summary>
    public string PostalPrefix => new string([.. PostalCode.Where(char.IsDigit)]) is { Length: >= 2 } digits
        ? digits[..2]
        : string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Line1)
        && !string.IsNullOrWhiteSpace(City)
        && !string.IsNullOrWhiteSpace(Region)
        && PostalPrefix.Length == 2;
}
