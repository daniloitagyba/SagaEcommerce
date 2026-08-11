using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20260811220000_AddSagaOutbox")]
public sealed class AddSagaOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE saga_outbox_messages (
                id uuid PRIMARY KEY,
                order_id uuid NOT NULL,
                topic varchar(200) NOT NULL,
                message_key varchar(200) NOT NULL,
                payload jsonb NOT NULL,
                correlation_id varchar(128) NOT NULL,
                trace_parent varchar(128) NULL,
                trace_state varchar(512) NULL,
                occurred_at timestamptz NOT NULL,
                attempt_count integer NOT NULL DEFAULT 0,
                next_attempt_at timestamptz NOT NULL,
                processed_at timestamptz NULL,
                last_error varchar(2000) NULL
            );

            CREATE INDEX ix_saga_outbox_messages_pending
                ON saga_outbox_messages (next_attempt_at, occurred_at)
                WHERE processed_at IS NULL;

            CREATE INDEX ix_saga_outbox_messages_order_id
                ON saga_outbox_messages (order_id, occurred_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "saga_outbox_messages");
    }
}
