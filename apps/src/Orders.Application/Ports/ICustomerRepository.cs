using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICustomerRepository
{
    /// <summary>Returns the customer, creating them on first sight.</summary>
    Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken);
}
