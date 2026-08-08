namespace Orders.Domain;

/// <summary>
/// Milestone 78: one row per order line being reserved, replacing
/// SagaOrchestrationState's single Sku/Quantity/ReservationId - a
/// multi-line order now reserves every line, not just the largest by
/// value. EF owns this table's schema only, same as SagaOrchestrationState;
/// runtime reads/writes go through raw Npgsql in SagaOrchestrationStore.
/// Reserved/Committed/Released are null until that line's reply arrives -
/// the reply consumer aggregates across every line for the same OrderId
/// before advancing the parent row's Step, since ReserveInventory now
/// means "waiting for every line," not "waiting for the one reservation."
/// </summary>
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
