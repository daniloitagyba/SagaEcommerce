using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICampaignRepository
{
    /// <summary>
    /// Returns the best eligible campaign.
    /// </summary>
    Task<CampaignSnapshot?> FindBestActiveAsync(
        decimal subtotal,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents a campaign budget claim.
/// </summary>
public sealed record CampaignReservation(string Code, decimal Amount, Guid OrderId, DateTimeOffset ReservedAt);

/// <summary>
/// Represents a campaign budget conflict.
/// </summary>
public sealed class CampaignBudgetUnavailableException(string code, string reason)
    : Exception($"Campaign '{code}' could not be applied: {reason}")
{
    public string Code { get; } = code;
}
