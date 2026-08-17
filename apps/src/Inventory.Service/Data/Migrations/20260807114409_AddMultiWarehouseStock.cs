using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Service.Data.Migrations
{
    public partial class AddMultiWarehouseStock : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reservation_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    warehouse_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    allocated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_allocations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "warehouse_stock",
                columns: table => new
                {
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    warehouse_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    available_quantity = table.Column<int>(type: "integer", nullable: false),
                    reserved_quantity = table.Column<int>(type: "integer", nullable: false),
                    reorder_point = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_warehouse_stock", x => new { x.sku, x.warehouse_code });
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_allocations_reservation",
                table: "reservation_allocations",
                column: "reservation_id");

            migrationBuilder.Sql("""
                INSERT INTO warehouse_stock (sku, warehouse_code, available_quantity, reserved_quantity, reorder_point, updated_at)
                SELECT sku, 'WH-SP', available_quantity - (available_quantity / 3), reserved_quantity, GREATEST(available_quantity / 10, 2), NOW()
                FROM inventory_items
                ON CONFLICT (sku, warehouse_code) DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO warehouse_stock (sku, warehouse_code, available_quantity, reserved_quantity, reorder_point, updated_at)
                SELECT sku, 'WH-RJ', available_quantity / 3, 0, 2, NOW()
                FROM inventory_items
                ON CONFLICT (sku, warehouse_code) DO NOTHING;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservation_allocations");

            migrationBuilder.DropTable(
                name: "warehouse_stock");
        }
    }
}
