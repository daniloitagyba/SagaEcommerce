namespace Payments.Service.Risk;

public interface IPaymentHistoryReader
{
    Task<PaymentHistory> ReadAsync(string customerId, CancellationToken cancellationToken);
}
