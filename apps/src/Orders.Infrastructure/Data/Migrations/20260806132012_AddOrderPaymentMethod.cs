using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderPaymentMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "saga_orchestration_states",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            // Same reasoning as the payments backfill: an order that predates
            // Milestone 68 was charged outright, so Pix is the honest reading
            // - and it keeps OrderStatusStore from asking Payments to capture
            // an authorization that never existed for historical orders.
            migrationBuilder.Sql("UPDATE orders SET payment_method = 'Pix' WHERE payment_method = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "orders");
        }
    }
}
