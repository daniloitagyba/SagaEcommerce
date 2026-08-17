using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20260816140000_AddOrderEventVersion")]
public sealed class AddOrderEventVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- A single cross-process, cross-transaction monotonic counter
            -- shared by every writer of OrderStatusChanged in this database
            -- (Orders.Worker's OrderStatusStore, and Orders.Api's
            -- EfOrderStatusRepository/EfOrderReturnRepository) - nextval()
            -- is atomic and lock-free by construction, which is exactly
            -- what lets two different service processes allocate from the
            -- same monotonic space without coordinating with each other
            -- beyond both talking to this one Postgres database. Replaces
            -- decided_at (a wall-clock timestamp each producer stamps
            -- independently) as the projection's ordering guard - see
            -- docs/roadmap-milestones-91-99.md, "the read-model ordering
            -- guard trusts physical clocks across services".
            CREATE SEQUENCE order_event_version_seq;

            ALTER TABLE order_summaries ADD COLUMN version bigint NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE order_summaries DROP COLUMN version;
            DROP SEQUENCE order_event_version_seq;
            """);
    }
}
