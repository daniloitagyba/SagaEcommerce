using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaOrchestrationLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quantity",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "reservation_id",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "sku",
                table: "saga_orchestration_states");

            migrationBuilder.CreateTable(
                name: "saga_orchestration_lines",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_index = table.Column<int>(type: "integer", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<bool>(type: "boolean", nullable: true),
                    committed = table.Column<bool>(type: "boolean", nullable: true),
                    released = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saga_orchestration_lines", x => new { x.order_id, x.line_index });
                    table.ForeignKey(
                        name: "FK_saga_orchestration_lines_saga_orchestration_states_order_id",
                        column: x => x.order_id,
                        principalTable: "saga_orchestration_states",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_saga_orchestration_lines_reservation_id",
                table: "saga_orchestration_lines",
                column: "reservation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saga_orchestration_lines");

            migrationBuilder.AddColumn<int>(
                name: "quantity",
                table: "saga_orchestration_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "reservation_id",
                table: "saga_orchestration_states",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "sku",
                table: "saga_orchestration_states",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");
        }
    }
}
