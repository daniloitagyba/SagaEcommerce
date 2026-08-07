using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentShippingPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shipping_postal_prefix",
                table: "payments",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shipping_postal_prefix",
                table: "payments");
        }
    }
}
