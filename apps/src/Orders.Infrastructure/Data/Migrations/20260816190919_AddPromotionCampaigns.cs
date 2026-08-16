using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "campaign_code",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "exclusivity_group",
                table: "coupons",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promotion_campaign_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_campaign_claims", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotion_campaigns",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    minimum_order_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    exclusivity_group = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    total_budget = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    budget_remaining = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_campaigns", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ux_promotion_campaign_claims_code_order",
                table: "promotion_campaign_claims",
                columns: ["code", "order_id"],
                unique: true);

            // One seeded campaign, the same reasoning AddCouponLifecycle
            // seeded SAVE10/SAVE20/HALFOFF: makes the feature demonstrable
            // out of the box rather than an empty table nobody discovers.
            // No exclusivity group - stacks with coupons and every
            // automatic promotion by default, the safest starting choice.
            migrationBuilder.Sql("""
                INSERT INTO promotion_campaigns (code, description, discount_amount, valid_from, valid_until,
                                                 minimum_order_amount, exclusivity_group, total_budget, budget_remaining)
                VALUES
                    ('FLASH20', 'R$20 off - flash sale, while the budget lasts', 20.00, NOW() - INTERVAL '1 day', NOW() + INTERVAL '365 days',
                     100.00, NULL, 500.00, 500.00)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_campaign_claims");

            migrationBuilder.DropTable(
                name: "promotion_campaigns");

            migrationBuilder.DropColumn(
                name: "campaign_code",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "exclusivity_group",
                table: "coupons");
        }
    }
}
