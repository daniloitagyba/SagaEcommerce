using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderLineTaxAndReturnPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows predate this milestone's refund-policy split and
            // never owed shipping under any category - Unwanted is the one
            // category that keeps them refunding exactly what they already
            // refunded, not retroactively granting a shipping refund they
            // never asked for.
            migrationBuilder.AddColumn<string>(
                name: "reason_category",
                table: "order_returns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unwanted");

            migrationBuilder.AddColumn<decimal>(
                name: "shipping_refund",
                table: "order_returns",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "line_tax",
                table: "order_lines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reason_category",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "shipping_refund",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "line_tax",
                table: "order_lines");
        }
    }
}
