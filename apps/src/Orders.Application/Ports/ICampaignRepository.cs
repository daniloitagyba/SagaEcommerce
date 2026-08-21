using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICampaignRepository
{
    Task<CampaignSnapshot?> FindBestActiveAsync(
        decimal subtotal,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record CampaignReservation(string Code, decimal Amount, Guid OrderId, DateTimeOffset ReservedAt);

public sealed class CampaignBudgetUnavailableException(string code, string reason)
    : Exception($"Campaign '{code}' could not be applied: {reason}")
{
    public string Code { get; } = code;
}
