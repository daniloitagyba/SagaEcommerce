using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orders.Domain;

namespace Orders.Infrastructure.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();

    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();

    public DbSet<SagaOrchestrationState> SagaOrchestrationStates => Set<SagaOrchestrationState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureOrder(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOrderSummary(modelBuilder);
        ConfigureOrderEvent(modelBuilder);
        ConfigureSagaOrchestrationState(modelBuilder);
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();

        order.ToTable("orders");
        order.HasKey(item => item.Id);
        order.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        order.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100).IsRequired();
        // Milestone 20, cutover phase: Amount now reads/writes amount_cents
        // directly via a value converter - the expand+dual-write phase
        // backfilled every row, so the old `amount` column (still present,
        // no longer touched) is safe to drop in the next, contract phase.
        order.Property(item => item.Amount)
            .HasColumnName("amount_cents")
            .HasConversion(
                amount => (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero),
                cents => cents / 100m)
            .IsRequired();
        order.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        order.Property(item => item.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        order.Property(item => item.CreatedAt).HasColumnName("created_at").IsRequired();
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
        var inbox = modelBuilder.Entity<InboxMessage>();

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
        inbox.HasIndex(item => item.ProcessedAt)
            .HasDatabaseName("ix_inbox_messages_processed_at");
    }

    private static void ConfigureOrderSummary(ModelBuilder modelBuilder)
    {
        var summary = modelBuilder.Entity<OrderSummary>();

        summary.ToTable("order_summaries");
        summary.HasKey(item => item.OrderId);
        summary.Property(item => item.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        summary.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100);
        summary.Property(item => item.Amount).HasColumnName("amount").HasPrecision(18, 2);
        summary.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3);
        summary.Property(item => item.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        summary.Property(item => item.OrderCreatedAt).HasColumnName("order_created_at");
        summary.Property(item => item.DecidedAt).HasColumnName("decided_at");
        summary.Property(item => item.ProjectedAt).HasColumnName("projected_at").IsRequired();
        summary.HasIndex(item => new { item.Status, item.OrderCreatedAt })
            .HasDatabaseName("ix_order_summaries_status");
    }

    private static void ConfigureOrderEvent(ModelBuilder modelBuilder)
    {
        var orderEvent = modelBuilder.Entity<OrderEvent>();

        orderEvent.ToTable("order_events");
        orderEvent.HasKey(item => item.Id);
        orderEvent.Property(item => item.Id).HasColumnName("id").ValueGeneratedOnAdd();
        orderEvent.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        orderEvent.Property(item => item.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        orderEvent.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        orderEvent.Property(item => item.OccurredAt).HasColumnName("occurred_at").IsRequired();
        orderEvent.HasIndex(item => new { item.OrderId, item.Id })
            .HasDatabaseName("ix_order_events_order_id");
    }

    private static void ConfigureSagaOrchestrationState(ModelBuilder modelBuilder)
    {
        var saga = modelBuilder.Entity<SagaOrchestrationState>();

        saga.ToTable("saga_orchestration_states");
        saga.HasKey(item => item.OrderId);
        saga.Property(item => item.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        saga.Property(item => item.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        saga.Property(item => item.RequestedAt).HasColumnName("requested_at").IsRequired();
        saga.Property(item => item.Step).HasColumnName("step").HasMaxLength(32).IsRequired();
        saga.Property(item => item.ReservationId).HasColumnName("reservation_id").IsRequired();
        saga.Property(item => item.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        saga.Property(item => item.Quantity).HasColumnName("quantity").IsRequired();
        saga.Property(item => item.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        saga.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        saga.HasIndex(item => item.RequestedAt)
            .HasDatabaseName("ix_saga_orchestration_states_requested_at");
    }
}

public sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Orders")
            ?? "Host=localhost;Database=orders;Username=orders";

        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new OrdersDbContext(options);
    }
}
