using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Service.Data.Migrations
{
    public partial class AddPaymentAuthorizationLifecycle : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "authorization_expires_at",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "method",
                table: "payments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "settled_at",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settlement_reason",
                table: "payments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "payments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_payments_pending_authorizations",
                table: "payments",
                columns: ["state", "authorization_expires_at"]);

            migrationBuilder.Sql("""
                UPDATE payments
                SET method = 'Pix',
                    state = CASE WHEN approved THEN 'Captured' ELSE 'Declined' END,
                    settled_at = CASE WHEN approved THEN decided_at ELSE settled_at END
                WHERE method = '' OR state = '';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payments_pending_authorizations",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "authorization_expires_at",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "method",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "settled_at",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "settlement_reason",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "state",
                table: "payments");
        }
    }
}
