using Orders.Domain;

namespace Orders.Application.Ports;

public interface ICustomerRepository
{
    /// <summary>
    /// Returns the customer, creating them on first sight.
    ///
    /// Auto-provisioning rather than a registration flow: this lab has no
    /// sign-up, and the first order a customer id places is the honest
    /// moment their account begins - which is what makes account age a real
    /// risk signal rather than a constant.
    /// </summary>
    Task<Customer> GetOrCreateAsync(string customerId, CancellationToken cancellationToken);
}
