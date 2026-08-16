using Inventory.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Service.Data.Migrations;

// Raw SQL, not an EF DbSet - see Orders.Infrastructure's identical
// migration for the same table, and OutboxPublisher.DeadLetterAsync for
// the writer.
[DbContext(typeof(InventoryDbContext))]
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
