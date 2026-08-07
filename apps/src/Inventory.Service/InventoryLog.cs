namespace Inventory.Service;

public sealed partial class InventoryLog
{
    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Decided reservation {ReservationId} for sku {Sku}: reserved={Reserved} with correlation {CorrelationId}")]
    public static partial void Decided(ILogger logger, Guid reservationId, string sku, bool reserved, string correlationId);

    [LoggerMessage(EventId = 9010, Level = LogLevel.Warning, Message = "Warehouse {WarehouseCode} fell to {AvailableQuantity} of {Sku}, at or below its reorder point of {ReorderPoint}")]
    public static partial void ReplenishmentNeeded(ILogger logger, string sku, string warehouseCode, int availableQuantity, int reorderPoint);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Information, Message = "Decided commit {ReservationId} for sku {Sku}: committed={Committed} with correlation {CorrelationId}")]
    public static partial void CommitDecided(ILogger logger, Guid reservationId, string sku, bool committed, string correlationId);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Information, Message = "Decided release {ReservationId} for sku {Sku}: released={Released} with correlation {CorrelationId}")]
    public static partial void ReleaseDecided(ILogger logger, Guid reservationId, string sku, bool released, string correlationId);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Information, Message = "Restocked {Sku} for return {ReturnId}: restocked={Restocked} with correlation {CorrelationId}")]
    public static partial void RestockDecided(ILogger logger, Guid returnId, string sku, bool restocked, string correlationId);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Information, Message = "Skipped duplicate reservation {ReservationId} for consumer {ConsumerName}")]
    public static partial void Duplicate(ILogger logger, Guid reservationId, string consumerName);

    [LoggerMessage(EventId = 9013, Level = LogLevel.Information, Message = "Reservation {ReservationId} for sku {Sku} backordered - the network cannot cover it yet, waiting for a restock (correlation {CorrelationId})")]
    public static partial void Backordered(ILogger logger, Guid reservationId, string sku, string correlationId);

    [LoggerMessage(EventId = 9014, Level = LogLevel.Information, Message = "Released backorder {ReservationId} for sku {Sku} on order {OrderId} after a restock (correlation {CorrelationId})")]
    public static partial void BackorderReleased(ILogger logger, Guid reservationId, string sku, Guid orderId, string correlationId);
}
