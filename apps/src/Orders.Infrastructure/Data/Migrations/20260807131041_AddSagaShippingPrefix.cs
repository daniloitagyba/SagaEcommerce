using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaShippingPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "shipping_postal_prefix",
                table: "saga_orchestration_states",
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
                table: "saga_orchestration_states");
        }
    }
}
