using BuildingBlocks;
using Orders.Application.Ports;

namespace Orders.Infrastructure.Observability;

public sealed class OpenTelemetryOrderMetrics : IOrderMetrics
{
    public void RecordCreated(string currency) => OrdersTelemetry.RecordCreated(currency);
}
