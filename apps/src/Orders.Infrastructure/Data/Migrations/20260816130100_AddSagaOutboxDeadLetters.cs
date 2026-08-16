using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations;

// The saga outbox's own dead-letter table - see AddSagaOutbox for why
// saga_outbox_messages itself is raw SQL, not an EF DbSet. Written to (via
// raw Npgsql) by Orders.Worker's SagaOutboxPublisher.MoveToDeadLetterAsync
// once a command exhausts SagaOrchestrationOptions.OutboxMaximumAttempts.
[DbContext(typeof(OrdersDbContext))]
[Migration("20260816130100_AddSagaOutboxDeadLetters")]
public sealed class AddSagaOutboxDeadLetters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE saga_outbox_dead_letters (
                id uuid PRIMARY KEY,
                order_id uuid NOT NULL,
                topic varchar(200) NOT NULL,
                message_key varchar(200) NOT NULL,
                payload jsonb NOT NULL,
                correlation_id varchar(128) NOT NULL,
                trace_parent varchar(128) NULL,
                trace_state varchar(512) NULL,
                occurred_at timestamptz NOT NULL,
                attempt_count integer NOT NULL,
                last_error varchar(2000) NULL,
                dead_lettered_at timestamptz NOT NULL
            );

            CREATE INDEX ix_saga_outbox_dead_letters_dead_lettered_at
                ON saga_outbox_dead_letters (dead_lettered_at);

            CREATE INDEX ix_saga_outbox_dead_letters_order_id
                ON saga_outbox_dead_letters (order_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "saga_outbox_dead_letters");
    }
}
