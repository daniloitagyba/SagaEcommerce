using BuildingBlocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Orders.Domain;

namespace Orders.Infrastructure.Data;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options), IOutboxDbContext
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<OrderReturn> OrderReturns => Set<OrderReturn>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();

    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();

    public DbSet<SagaOrchestrationState> SagaOrchestrationStates => Set<SagaOrchestrationState>();

    public DbSet<SagaOrchestrationLine> SagaOrchestrationLines => Set<SagaOrchestrationLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureOrder(modelBuilder);
        ConfigureOrderLine(modelBuilder);
        ConfigureCustomer(modelBuilder);
        ConfigureOrderReturn(modelBuilder);
        ConfigureCoupon(modelBuilder);
        ConfigureOutbox(modelBuilder);
        ConfigureInbox(modelBuilder);
        ConfigureOrderSummary(modelBuilder);
        ConfigureOrderEvent(modelBuilder);
        ConfigureSagaOrchestrationState(modelBuilder);
        ConfigureSagaOrchestrationLine(modelBuilder);
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

        // Milestone 66: the pricing breakdown. Stored denormalised on the
        // order rather than recomputed on read, because a promotion is a
        // point-in-time fact - re-running today's rules against a
        // six-month-old order would produce today's prices, not what the
        // customer was actually charged.
        order.Property(item => item.Subtotal).HasColumnName("subtotal").HasPrecision(18, 2).IsRequired();
        order.Property(item => item.DiscountTotal).HasColumnName("discount_total").HasPrecision(18, 2).IsRequired();
        order.Property(item => item.ShippingTotal).HasColumnName("shipping_total").HasPrecision(18, 2).IsRequired();
        order.Property(item => item.TaxTotal).HasColumnName("tax_total").HasPrecision(18, 2).IsRequired();
        order.Property(item => item.CouponCode).HasColumnName("coupon_code").HasMaxLength(64);
        order.Property(item => item.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16).IsRequired();

        // Owned rather than a separate table: an address belongs to the
        // order it shipped to and is never queried independently. Storing a
        // snapshot also means a customer editing their address later cannot
        // rewrite where a past order was actually sent.
        order.OwnsOne(item => item.ShippingAddress, address =>
        {
            address.Property(value => value.Line1).HasColumnName("ship_line1").HasMaxLength(200);
            address.Property(value => value.City).HasColumnName("ship_city").HasMaxLength(100);
            address.Property(value => value.Region).HasColumnName("ship_region").HasMaxLength(8);
            address.Property(value => value.PostalCode).HasColumnName("ship_postal_code").HasMaxLength(16);
        });

        order.HasMany(item => item.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
        order.Navigation(item => item.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureOrderLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<OrderLine>();

        line.ToTable("order_lines");
        line.HasKey(item => item.Id);
        line.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        line.Property(item => item.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        line.Property(item => item.ProductName).HasColumnName("product_name").HasMaxLength(256).IsRequired();
        line.Property(item => item.CategorySlug).HasColumnName("category_slug").HasMaxLength(64).IsRequired();
        line.Property(item => item.Quantity).HasColumnName("quantity").IsRequired();
        line.Property(item => item.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2).IsRequired();
        line.Property(item => item.LineSubtotal).HasColumnName("line_subtotal").HasPrecision(18, 2).IsRequired();
        line.Property(item => item.LineDiscount).HasColumnName("line_discount").HasPrecision(18, 2).IsRequired();
        line.Property(item => item.LineTotal).HasColumnName("line_total").HasPrecision(18, 2).IsRequired();
        line.HasIndex(item => item.OrderId).HasDatabaseName("ix_order_lines_order_id");
        line.HasIndex(item => item.Sku).HasDatabaseName("ix_order_lines_sku");
        line.Property(item => item.ReturnedQuantity).HasColumnName("returned_quantity").IsRequired();
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();

        customer.ToTable("customers");
        customer.HasKey(item => item.Id);
        customer.Property(item => item.Id).HasColumnName("id").HasMaxLength(100).ValueGeneratedNever();
        customer.Property(item => item.Tier).HasColumnName("tier").HasMaxLength(16).IsRequired();
        customer.Property(item => item.LifetimeSpend).HasColumnName("lifetime_spend").HasPrecision(18, 2).IsRequired();
        customer.Property(item => item.CompletedOrderCount).HasColumnName("completed_order_count").IsRequired();
        customer.Property(item => item.CreatedAt).HasColumnName("created_at").IsRequired();
    }

    private static void ConfigureOrderReturn(ModelBuilder modelBuilder)
    {
        var orderReturn = modelBuilder.Entity<OrderReturn>();

        orderReturn.ToTable("order_returns");
        orderReturn.HasKey(item => item.Id);
        orderReturn.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        orderReturn.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        orderReturn.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100).IsRequired();
        orderReturn.Property(item => item.Reason).HasColumnName("reason").HasMaxLength(256).IsRequired();
        orderReturn.Property(item => item.RefundTotal).HasColumnName("refund_total").HasPrecision(18, 2).IsRequired();
        orderReturn.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        orderReturn.Property(item => item.RequestedAt).HasColumnName("requested_at").IsRequired();
        orderReturn.HasIndex(item => item.OrderId).HasDatabaseName("ix_order_returns_order_id");
        orderReturn.HasMany(item => item.Lines).WithOne().HasForeignKey(line => line.ReturnId).OnDelete(DeleteBehavior.Cascade);
        orderReturn.Navigation(item => item.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        var returnLine = modelBuilder.Entity<OrderReturnLine>();
        returnLine.ToTable("order_return_lines");
        returnLine.HasKey(item => item.Id);
        returnLine.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        returnLine.Property(item => item.ReturnId).HasColumnName("return_id").IsRequired();
        returnLine.Property(item => item.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        returnLine.Property(item => item.Quantity).HasColumnName("quantity").IsRequired();
        returnLine.Property(item => item.RefundAmount).HasColumnName("refund_amount").HasPrecision(18, 2).IsRequired();
    }

    private static void ConfigureCoupon(ModelBuilder modelBuilder)
    {
        var coupon = modelBuilder.Entity<Coupon>();

        coupon.ToTable("coupons");
        coupon.HasKey(item => item.Code);
        coupon.Property(item => item.Code).HasColumnName("code").HasMaxLength(64).ValueGeneratedNever();
        coupon.Property(item => item.Description).HasColumnName("description").HasMaxLength(256).IsRequired();
        coupon.Property(item => item.Percentage).HasColumnName("percentage").HasPrecision(5, 2).IsRequired();
        coupon.Property(item => item.ValidFrom).HasColumnName("valid_from").IsRequired();
        coupon.Property(item => item.ValidUntil).HasColumnName("valid_until").IsRequired();
        coupon.Property(item => item.MinimumOrderAmount).HasColumnName("minimum_order_amount").HasPrecision(18, 2).IsRequired();
        coupon.Property(item => item.MaxTotalRedemptions).HasColumnName("max_total_redemptions");
        coupon.Property(item => item.MaxPerCustomer).HasColumnName("max_per_customer");
        coupon.Property(item => item.RedemptionCount).HasColumnName("redemption_count").IsRequired();

        var redemption = modelBuilder.Entity<CouponRedemption>();

        redemption.ToTable("coupon_redemptions");
        redemption.HasKey(item => item.Id);
        redemption.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        redemption.Property(item => item.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        redemption.Property(item => item.OrderId).HasColumnName("order_id").IsRequired();
        redemption.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100).IsRequired();
        redemption.Property(item => item.State).HasColumnName("state").HasMaxLength(16).IsRequired();
        redemption.Property(item => item.ReservedAt).HasColumnName("reserved_at").IsRequired();
        redemption.Property(item => item.SettledAt).HasColumnName("settled_at");

        // One order redeems a given coupon at most once. This is not
        // cosmetic: it is what makes a retried checkout unable to claim a
        // second slot for the same order, in the database rather than by
        // application convention.
        redemption.HasIndex(item => new { item.Code, item.OrderId })
            .IsUnique()
            .HasDatabaseName("ux_coupon_redemptions_code_order");
        // Covers the per-customer limit count, which runs on the checkout
        // hot path inside the reservation transaction.
        redemption.HasIndex(item => new { item.Code, item.CustomerId, item.State })
            .HasDatabaseName("ix_coupon_redemptions_customer");
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
        saga.Property(item => item.CustomerId).HasColumnName("customer_id").HasMaxLength(100).IsRequired();
        saga.Property(item => item.PaymentMethod).HasColumnName("payment_method").HasMaxLength(16).IsRequired();
        saga.Property(item => item.ShippingPostalPrefix).HasColumnName("shipping_postal_prefix").HasMaxLength(8).IsRequired();
        saga.Property(item => item.RequestedAt).HasColumnName("requested_at").IsRequired();
        saga.Property(item => item.Step).HasColumnName("step").HasMaxLength(32).IsRequired();
        saga.Property(item => item.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
        saga.Property(item => item.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        saga.HasIndex(item => item.RequestedAt)
            .HasDatabaseName("ix_saga_orchestration_states_requested_at");
    }

    private static void ConfigureSagaOrchestrationLine(ModelBuilder modelBuilder)
    {
        var line = modelBuilder.Entity<SagaOrchestrationLine>();

        line.ToTable("saga_orchestration_lines");
        line.HasKey(item => new { item.OrderId, item.LineIndex });
        line.Property(item => item.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        line.Property(item => item.LineIndex).HasColumnName("line_index").ValueGeneratedNever();
        line.Property(item => item.ReservationId).HasColumnName("reservation_id").IsRequired();
        line.Property(item => item.Sku).HasColumnName("sku").HasMaxLength(64).IsRequired();
        line.Property(item => item.Quantity).HasColumnName("quantity").IsRequired();
        line.Property(item => item.Reserved).HasColumnName("reserved");
        line.Property(item => item.Committed).HasColumnName("committed");
        line.Property(item => item.Released).HasColumnName("released");
        line.HasOne<SagaOrchestrationState>().WithMany().HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Cascade);
        line.HasIndex(item => item.ReservationId)
            .IsUnique()
            .HasDatabaseName("ix_saga_orchestration_lines_reservation_id");
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
