using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Payments.Service.Domain;

namespace Payments.Service.Data;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigurePayment(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureInbox(modelBuilder);
    }

    private static void ConfigurePayment(ModelBuilder modelBuilder)
    {
        var payment = modelBuilder.Entity<Payment>();

        payment.ToTable("payments");
        payment.HasKey(item => item.Id);
        payment.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        payment.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        payment.Property(item => item.IsPrimary).HasColumnName("is_primary").IsRequired();
        payment.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100).IsRequired();
        payment.Property(item => item.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        payment.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        payment.Property(item => item.Approved).HasColumnName("approved").IsRequired();
        payment.Property(item => item.DecidedAt).HasColumnName("decided_at").IsRequired();
        payment.Property(item => item.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        payment.Property(item => item.Method).HasColumnName("method").HasMaxLength(16).IsRequired();
        payment.Property(item => item.ShippingPostalPrefix).HasColumnName("shipping_postal_prefix").HasMaxLength(8).IsRequired();
        payment.Property(item => item.State).HasColumnName("state").HasMaxLength(16).IsRequired();
        payment.Property(item => item.AuthorizationExpiresAt).HasColumnName("authorization_expires_at");
        payment.Property(item => item.SettledAt).HasColumnName("settled_at");
        payment.Property(item => item.SettlementReason).HasColumnName("settlement_reason").HasMaxLength(256);
        payment.Property(item => item.RefundedAmount).HasColumnName("refunded_amount").HasPrecision(18, 2).IsRequired();
        payment.HasIndex(item => new { item.State, item.AuthorizationExpiresAt })
            .HasDatabaseName("ix_payments_pending_authorizations");
        payment.HasIndex(item => item.OrderId)
            .IsUnique()
            .HasDatabaseName("ux_payments_primary_order_id")
            .HasFilter("is_primary");
        payment.HasIndex(item => new { item.CustomerId, item.DecidedAt })
            .HasDatabaseName("ix_payments_customer_history");
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

public sealed class PaymentsDbContextFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Payments")
            ?? "Host=localhost;Database=payments;Username=orders";

        var options = new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PaymentsDbContext(options);
    }
}
