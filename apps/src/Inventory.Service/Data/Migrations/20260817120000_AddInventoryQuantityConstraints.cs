using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Service.Data.Migrations;

public partial class AddInventoryQuantityConstraints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_items_available_quantity_non_negative",
            table: "inventory_items",
            sql: "available_quantity >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_items_reserved_quantity_non_negative",
            table: "inventory_items",
            sql: "reserved_quantity >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_stock_available_quantity_non_negative",
            table: "warehouse_stock",
            sql: "available_quantity >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_stock_reserved_quantity_non_negative",
            table: "warehouse_stock",
            sql: "reserved_quantity >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_warehouse_stock_reorder_point_non_negative",
            table: "warehouse_stock",
            sql: "reorder_point >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_reservation_allocations_quantity_positive",
            table: "reservation_allocations",
            sql: "quantity > 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_reservation_ledger_quantity_positive",
            table: "inventory_reservation_ledger",
            sql: "quantity > 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_backorders_quantity_positive",
            table: "backorders",
            sql: "quantity > 0");

        migrationBuilder.AddCheckConstraint(
            name: "ck_purchase_orders_quantity_positive",
            table: "purchase_orders",
            sql: "quantity > 0");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_inventory_items_available_quantity_non_negative", "inventory_items");
        migrationBuilder.DropCheckConstraint("ck_inventory_items_reserved_quantity_non_negative", "inventory_items");
        migrationBuilder.DropCheckConstraint("ck_warehouse_stock_available_quantity_non_negative", "warehouse_stock");
        migrationBuilder.DropCheckConstraint("ck_warehouse_stock_reserved_quantity_non_negative", "warehouse_stock");
        migrationBuilder.DropCheckConstraint("ck_warehouse_stock_reorder_point_non_negative", "warehouse_stock");
        migrationBuilder.DropCheckConstraint("ck_reservation_allocations_quantity_positive", "reservation_allocations");
        migrationBuilder.DropCheckConstraint("ck_inventory_reservation_ledger_quantity_positive", "inventory_reservation_ledger");
        migrationBuilder.DropCheckConstraint("ck_backorders_quantity_positive", "backorders");
        migrationBuilder.DropCheckConstraint("ck_purchase_orders_quantity_positive", "purchase_orders");
    }
}
