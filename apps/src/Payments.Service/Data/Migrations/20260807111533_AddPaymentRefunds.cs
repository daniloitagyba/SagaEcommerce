using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Service.Data.Migrations
{
    public partial class AddPaymentRefunds : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "refunded_amount",
                table: "payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refunded_amount",
                table: "payments");
        }
    }
}
