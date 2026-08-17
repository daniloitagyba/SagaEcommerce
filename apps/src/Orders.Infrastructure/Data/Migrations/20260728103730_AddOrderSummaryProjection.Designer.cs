using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    [DbContext(typeof(OrdersDbContext))]
    [Migration("20260728103730_AddOrderSummaryProjection")]
    partial class AddOrderSummaryProjection
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.4")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Orders.Domain.InboxMessage", b =>
                {
                    b.Property<string>("ConsumerName")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)")
                        .HasColumnName("consumer_name");

                    b.Property<Guid>("EventId")
                        .HasColumnType("uuid")
                        .HasColumnName("event_id");

                    b.Property<string>("CorrelationId")
                        .IsRequired()
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)")
                        .HasColumnName("correlation_id");

                    b.Property<long>("Offset")
                        .HasColumnType("bigint")
                        .HasColumnName("offset");

                    b.Property<int>("Partition")
                        .HasColumnType("integer")
                        .HasColumnName("partition");

                    b.Property<DateTimeOffset>("ProcessedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("processed_at");

                    b.Property<string>("Topic")
                        .IsRequired()
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("topic");

                    b.HasKey("ConsumerName", "EventId");

                    b.HasIndex("ProcessedAt")
                        .HasDatabaseName("ix_inbox_messages_processed_at");

                    b.HasIndex("ConsumerName", "Topic", "Partition", "Offset")
                        .HasDatabaseName("ix_inbox_messages_source_position");

                    b.ToTable("inbox_messages", (string)null);
                });

            modelBuilder.Entity("Orders.Domain.Order", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<decimal>("Amount")
                        .HasPrecision(18, 2)
                        .HasColumnType("numeric(18,2)")
                        .HasColumnName("amount");

                    b.Property<DateTimeOffset>("CreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("created_at");

                    b.Property<string>("Currency")
                        .IsRequired()
                        .HasMaxLength(3)
                        .HasColumnType("character varying(3)")
                        .HasColumnName("currency");

                    b.Property<string>("CustomerId")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("customer_id");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("status");

                    b.HasKey("Id");

                    b.ToTable("orders", (string)null);
                });

            modelBuilder.Entity("Orders.Domain.OrderSummary", b =>
                {
                    b.Property<Guid>("OrderId")
                        .HasColumnType("uuid")
                        .HasColumnName("order_id");

                    b.Property<decimal?>("Amount")
                        .HasPrecision(18, 2)
                        .HasColumnType("numeric(18,2)")
                        .HasColumnName("amount");

                    b.Property<string>("Currency")
                        .HasMaxLength(3)
                        .HasColumnType("character varying(3)")
                        .HasColumnName("currency");

                    b.Property<string>("CustomerId")
                        .HasMaxLength(100)
                        .HasColumnType("character varying(100)")
                        .HasColumnName("customer_id");

                    b.Property<DateTimeOffset?>("DecidedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("decided_at");

                    b.Property<DateTimeOffset?>("OrderCreatedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("order_created_at");

                    b.Property<DateTimeOffset>("ProjectedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("projected_at");

                    b.Property<string>("Status")
                        .IsRequired()
                        .HasMaxLength(32)
                        .HasColumnType("character varying(32)")
                        .HasColumnName("status");

                    b.HasKey("OrderId");

                    b.HasIndex("Status", "OrderCreatedAt")
                        .HasDatabaseName("ix_order_summaries_status");

                    b.ToTable("order_summaries", (string)null);
                });

            modelBuilder.Entity("Orders.Domain.OutboxMessage", b =>
                {
                    b.Property<Guid>("Id")
                        .HasColumnType("uuid")
                        .HasColumnName("id");

                    b.Property<int>("AttemptCount")
                        .HasColumnType("integer")
                        .HasColumnName("attempt_count");

                    b.Property<string>("CorrelationId")
                        .IsRequired()
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)")
                        .HasColumnName("correlation_id");

                    b.Property<string>("EventType")
                        .IsRequired()
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("event_type");

                    b.Property<string>("LastError")
                        .HasMaxLength(2000)
                        .HasColumnType("character varying(2000)")
                        .HasColumnName("last_error");

                    b.Property<DateTimeOffset>("NextAttemptAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("next_attempt_at");

                    b.Property<DateTimeOffset>("OccurredAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("occurred_at");

                    b.Property<string>("Payload")
                        .IsRequired()
                        .HasColumnType("jsonb")
                        .HasColumnName("payload");

                    b.Property<DateTimeOffset?>("ProcessedAt")
                        .HasColumnType("timestamp with time zone")
                        .HasColumnName("processed_at");

                    b.Property<string>("TraceParent")
                        .HasMaxLength(256)
                        .HasColumnType("character varying(256)")
                        .HasColumnName("trace_parent");

                    b.Property<string>("TraceState")
                        .HasMaxLength(512)
                        .HasColumnType("character varying(512)")
                        .HasColumnName("trace_state");

                    b.HasKey("Id");

                    b.HasIndex("ProcessedAt", "NextAttemptAt", "OccurredAt")
                        .HasDatabaseName("ix_outbox_messages_pending")
                        .HasFilter("processed_at IS NULL");

                    b.ToTable("outbox_messages", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
