namespace Orders.Domain;

/// <summary>One row per order line being reserved by the saga; Reserved/Committed/Released are null until that line's reply arrives.</summary>
public sealed class SagaOrchestrationLine
{
    private SagaOrchestrationLine()
    {
    }

    public Guid OrderId { get; private set; }

    public int LineIndex { get; private set; }

    public Guid ReservationId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int Quantity { get; private set; }

    public bool? Reserved { get; private set; }

    public bool? Committed { get; private set; }

    public bool? Released { get; private set; }
}
