using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    public partial class AddSagaStepTracking : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "amount",
                table: "saga_orchestration_states",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "saga_orchestration_states",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

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

            migrationBuilder.AddColumn<string>(
                name: "step",
                table: "saga_orchestration_states",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "amount",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "quantity",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "reservation_id",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "sku",
                table: "saga_orchestration_states");

            migrationBuilder.DropColumn(
                name: "step",
                table: "saga_orchestration_states");
        }
    }
}
