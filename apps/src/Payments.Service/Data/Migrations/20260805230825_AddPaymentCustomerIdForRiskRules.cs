using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentCustomerIdForRiskRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_id",
                table: "payments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_payments_customer_history",
                table: "payments",
                columns: ["customer_id", "decided_at"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_customer_history",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "payments");
        }
    }
}
