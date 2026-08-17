using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Inventory.Service.Domain;

namespace Inventory.Service.Data;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    public DbSet<WarehouseStock> WarehouseStocks => Set<WarehouseStock>();

    public DbSet<ReservationAllocation> ReservationAllocations => Set<ReservationAllocation>();

    public DbSet<InventoryReservationLedgerEntry> ReservationLedgerEntries => Set<InventoryReservationLedgerEntry>();

    public DbSet<Backorder> Backorders => Set<Backorder>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureInventoryItem(modelBuilder);
        ConfigureWarehouseStock(modelBuilder);
        ConfigureReservationLedgerEntry(modelBuilder);
        ConfigureBackorder(modelBuilder);
        ConfigurePurchaseOrder(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureInbox(modelBuilder);
    }

    private static void ConfigureInventoryItem(ModelBuilder modelBuilder)
    {
        var item = modelBuilder.Entity<InventoryItem>();

        item.ToTable("inventory_items");
        item.HasKey(entity => entity.Sku);
        item.Property(entity => entity.Sku).HasColumnName("sku").HasMaxLength(64).ValueGeneratedNever();
        item.Property(entity => entity.AvailableQuantity).HasColumnName("available_quantity").IsRequired();
        item.Property(entity => entity.ReservedQuantity).HasColumnName("reserved_quantity").IsRequired();
        item.Property(entity => entity.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }

    private static void ConfigureWarehouseStock(ModelBuilder modelBuilder)
    {
        var stock = modelBuilder.Entity<WarehouseStock>();

        stock.ToTable("warehouse_stock");
        stock.HasKey(entity => new { entity.Sku, entity.WarehouseCode });
        stock.Property(entity => entity.Sku).HasColumnName("sku").HasMaxLength(64);
        stock.Property(entity => entity.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(16);
        stock.Property(entity => entity.AvailableQuantity).HasColumnName("available_quantity").IsRequired();
        stock.Property(entity => entity.ReservedQuantity).HasColumnName("reserved_quantity").IsRequired();
        stock.Property(entity => entity.ReorderPoint).HasColumnName("reorder_point").IsRequired();
        stock.Property(entity => entity.UpdatedAt).HasColumnName("updated_at").IsRequired();

        var allocation = modelBuilder.Entity<ReservationAllocation>();

        allocation.ToTable("reservation_allocations");
        allocation.HasKey(entity => entity.Id);
        allocation.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        allocation.Property(entity => entity.ReservationId).HasColumnName("reservation_id").IsRequired();
        allocation.Property(entity => entity.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        allocation.Property(entity => entity.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(16).IsRequired();
        allocation.Property(entity => entity.Quantity).HasColumnName("quantity").IsRequired();
        allocation.Property(entity => entity.AllocatedAt).HasColumnName("allocated_at").IsRequired();
        allocation.HasIndex(entity => entity.ReservationId).HasDatabaseName("ix_reservation_allocations_reservation");
    }

    private static void ConfigureReservationLedgerEntry(ModelBuilder modelBuilder)
    {
        var entry = modelBuilder.Entity<InventoryReservationLedgerEntry>();

        entry.ToTable("inventory_reservation_ledger");
        entry.HasKey(item => item.Id);
        entry.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        entry.Property(item => item.ReservationId).HasColumnName("reservation_id").IsRequired();
        entry.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        entry.Property(item => item.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        entry.Property(item => item.Quantity).HasColumnName("quantity").IsRequired();
        entry.Property(item => item.CommittedAt).HasColumnName("committed_at").IsRequired();
        entry.HasIndex(item => new { item.OrderId, item.Sku })
            .HasDatabaseName("ix_inventory_reservation_ledger_order_sku");
    }

    private static void ConfigureBackorder(ModelBuilder modelBuilder)
    {
        var backorder = modelBuilder.Entity<Backorder>();

        backorder.ToTable("backorders");
        backorder.HasKey(entity => entity.ReservationId);
        backorder.Property(entity => entity.ReservationId).HasColumnName("reservation_id").ValueGeneratedNever();
        backorder.Property(entity => entity.OrderId).HasColumnName("order_id").IsRequired();
        backorder.Property(entity => entity.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        backorder.Property(entity => entity.Quantity).HasColumnName("quantity").IsRequired();
        backorder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        backorder.Property(entity => entity.RequestedAt).HasColumnName("requested_at").IsRequired();
        backorder.HasIndex(entity => new { entity.Sku, entity.RequestedAt })
            .HasDatabaseName("ix_backorders_sku_requested_at");
    }

    private static void ConfigurePurchaseOrder(ModelBuilder modelBuilder)
    {
        var purchaseOrder = modelBuilder.Entity<PurchaseOrder>();

        purchaseOrder.ToTable("purchase_orders");
        purchaseOrder.HasKey(entity => entity.Id);
        purchaseOrder.Property(entity => entity.Id).HasColumnName("id").ValueGeneratedNever();
        purchaseOrder.Property(entity => entity.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        purchaseOrder.Property(entity => entity.WarehouseCode).HasColumnName("warehouse_code").HasMaxLength(16).IsRequired();
        purchaseOrder.Property(entity => entity.Quantity).HasColumnName("quantity").IsRequired();
        purchaseOrder.Property(entity => entity.State).HasColumnName("state").HasMaxLength(16).IsRequired();
        purchaseOrder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        purchaseOrder.Property(entity => entity.RequestedAt).HasColumnName("requested_at").IsRequired();
        purchaseOrder.Property(entity => entity.ReceivedAt).HasColumnName("received_at");
        purchaseOrder.HasIndex(entity => new { entity.State, entity.RequestedAt })
            .HasDatabaseName("ix_purchase_orders_state_requested_at");
    }

    private static void ConfigureOutbox(ModelBuilder modelBuilder)
    {
        var outbox = modelBuilder.Entity<OutboxMessage>();

        outbox.ToTable("outbox_messages");
        outbox.HasKey(item => item.Id);
        outbox.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        outbox.Property(item => item.EventType).HasColumnName("event_type").HasMaxLength(256).IsRequired();
        outbox.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        outbox.Property(item => item.OccurredAt).HasColumnName("occurred_at").IsRequired();
        outbox.Property(item => item.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        outbox.Property(item => item.TraceParent).HasColumnName("trace_parent").HasMaxLength(256);
        outbox.Property(item => item.TraceState).HasColumnName("trace_state").HasMaxLength(512);
        outbox.Property(item => item.AttemptCount).HasColumnName("attempt_count").IsRequired();
        outbox.Property(item => item.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
        outbox.Property(item => item.ProcessedAt).HasColumnName("processed_at");
        outbox.Property(item => item.LastError).HasColumnName("last_error").HasMaxLength(2_000);
        outbox.HasIndex(item => new { item.ProcessedAt, item.NextAttemptAt, item.OccurredAt })
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_at IS NULL");
    }

    private static void ConfigureInbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxRecord>(inbox =>
        {
            inbox.ToTable("inbox_messages");
            inbox.HasKey(item => new { item.ConsumerName, item.EventId });
            inbox.Property(item => item.ConsumerName).HasColumnName("consumer_name").HasMaxLength(128).IsRequired();
            inbox.Property(item => item.EventId).HasColumnName("event_id").ValueGeneratedNever();
            inbox.Property(item => item.Topic).HasColumnName("topic").HasMaxLength(256).IsRequired();
            inbox.Property(item => item.Partition).HasColumnName("partition").IsRequired();
            inbox.Property(item => item.Offset).HasColumnName("offset").IsRequired();
            inbox.Property(item => item.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
            inbox.Property(item => item.ProcessedAt).HasColumnName("processed_at").IsRequired();
            inbox.HasIndex(item => new { item.ConsumerName, item.Topic, item.Partition, item.Offset })
                .HasDatabaseName("ix_inbox_messages_source_position");
        });
    }
}

public sealed class InboxRecord
{
    public string ConsumerName { get; init; } = string.Empty;

    public Guid EventId { get; init; }

    public string Topic { get; init; } = string.Empty;

    public int Partition { get; init; }

    public long Offset { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; init; }
}

public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    public InventoryDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Inventory")
            ?? "Host=localhost;Database=inventory;Username=orders";

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new InventoryDbContext(options);
    }
}
