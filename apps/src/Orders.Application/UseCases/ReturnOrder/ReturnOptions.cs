namespace Orders.Application.UseCases.ReturnOrder;

/// <summary>
/// Milestone 82: how long after checkout a <see cref="Orders.Domain.ReturnReasonCategory.Regret"/>
/// return still owes shipping - Brazil's CDC art. 49 gives a shopper seven
/// days to change their mind with no reason required, and the return owes
/// the full amount, outbound shipping included. Configurable rather than a
/// literal 7 in the domain, the same reasoning PricingOptions applies to
/// promotion policy: the <em>rule</em> ("Regret has a window") is code;
/// how long the window is is data.
/// </summary>
public sealed class ReturnOptions
{
    public const string SectionName = "Returns";

    public int RegretWindowDays { get; init; } = 7;
}
