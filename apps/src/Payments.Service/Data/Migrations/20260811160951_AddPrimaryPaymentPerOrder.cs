using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Service.Data.Migrations
{
    public partial class AddPrimaryPaymentPerOrder : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_order_id",
                table: "payments");

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "payments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY order_id
                               ORDER BY decided_at DESC, id DESC) AS row_number
                    FROM payments
                )
                UPDATE payments AS payment
                SET is_primary = (ranked.row_number = 1)
                FROM ranked
                WHERE payment.id = ranked.id;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_payments_primary_order_id",
                table: "payments",
                column: "order_id",
                unique: true,
                filter: "is_primary");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payments_primary_order_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "payments");

            migrationBuilder.CreateIndex(
                name: "ix_payments_order_id",
                table: "payments",
                column: "order_id");
        }
    }
}
