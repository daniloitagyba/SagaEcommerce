namespace Orders.Application.Ports;

public interface IOrderMetrics
{
    void RecordCreated(string currency);
}

public sealed class NullOrderMetrics : IOrderMetrics
{
    public static readonly NullOrderMetrics Instance = new();

    private NullOrderMetrics()
    {
    }

    public void RecordCreated(string currency)
    {
    }
}
