using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    public partial class AddOrderPaymentMethod : Migration
    {
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

            migrationBuilder.Sql("UPDATE orders SET payment_method = 'Pix' WHERE payment_method = '';");
        }

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
