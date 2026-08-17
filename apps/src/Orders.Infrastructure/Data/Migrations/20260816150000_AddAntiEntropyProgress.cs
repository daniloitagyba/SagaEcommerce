using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orders.Infrastructure.Data;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations;

[DbContext(typeof(OrdersDbContext))]
[Migration("20260816150000_AddAntiEntropyProgress")]
public sealed class AddAntiEntropyProgress : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE anti_entropy_progress (
                check_name varchar(100) PRIMARY KEY,
                cursor_created_at timestamptz NOT NULL,
                cursor_id uuid NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "anti_entropy_progress");
    }
}
