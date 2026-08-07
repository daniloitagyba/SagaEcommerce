using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICustomerRepository
{
    /// <summary>Returns the customer, creating them on first sight - this lab has no sign-up flow, so the first order is when the account begins.</summary>
    Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken);
}
