using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20260816130000_AddOutboxDeadLetters")]
public sealed class AddOutboxDeadLetters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE outbox_dead_letters (
                id uuid PRIMARY KEY,
                event_type varchar(200) NOT NULL,
                payload text NOT NULL,
                occurred_at timestamptz NOT NULL,
                correlation_id varchar(128) NOT NULL,
                trace_parent varchar(128) NULL,
                trace_state varchar(512) NULL,
                attempt_count integer NOT NULL,
                last_error varchar(2000) NULL,
                dead_lettered_at timestamptz NOT NULL
            );

            CREATE INDEX ix_outbox_dead_letters_dead_lettered_at
                ON outbox_dead_letters (dead_lettered_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "outbox_dead_letters");
    }
}
