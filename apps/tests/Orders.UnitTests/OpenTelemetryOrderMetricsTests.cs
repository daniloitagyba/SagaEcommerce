using Orders.Infrastructure.Observability;

namespace Orders.UnitTests;

public sealed class OpenTelemetryOrderMetricsTests
{
    [Fact]
    public void RecordCreatedDoesNotThrow()
    {
        var metrics = new OpenTelemetryOrderMetrics();

        var exception = Record.Exception(() => metrics.RecordCreated("USD"));

        Assert.Null(exception);
    }
}
