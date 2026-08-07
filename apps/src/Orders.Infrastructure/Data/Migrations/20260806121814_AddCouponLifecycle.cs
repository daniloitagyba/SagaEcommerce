using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCouponLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupon_redemptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "coupons",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    minimum_order_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    max_total_redemptions = table.Column<int>(type: "integer", nullable: true),
                    max_per_customer = table.Column<int>(type: "integer", nullable: true),
                    redemption_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coupons", x => x.code);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_customer",
                table: "coupon_redemptions",
                columns: ["code", "customer_id", "state"]);

            migrationBuilder.CreateIndex(
                name: "ux_coupon_redemptions_code_order",
                table: "coupon_redemptions",
                columns: ["code", "order_id"],
                unique: true);

            // Seeds the three codes Milestone 66 shipped as configuration,
            // so every existing reference to them (the storefront's coupon
            // placeholder, the README, the pricing docs) keeps working -
            // they are simply rows now, with the limits config could never
            // express. HALFOFF gets the tightest ones deliberately: it is
            // the 50%-off code, and the one a leak would hurt most.
            migrationBuilder.Sql("""
                INSERT INTO coupons (code, description, percentage, valid_from, valid_until,
                                     minimum_order_amount, max_total_redemptions, max_per_customer, redemption_count)
                VALUES
                    ('SAVE10',  '10% off your order',  10.00, NOW() - INTERVAL '1 day', NOW() + INTERVAL '365 days',   0.00, NULL,  NULL, 0),
                    ('SAVE20',  '20% off orders over 200',  20.00, NOW() - INTERVAL '1 day', NOW() + INTERVAL '365 days', 200.00, 1000,    5, 0),
                    ('HALFOFF', '50% off - launch promotion', 50.00, NOW() - INTERVAL '1 day', NOW() + INTERVAL '30 days',  100.00,  100,    1, 0)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coupon_redemptions");

            migrationBuilder.DropTable(
                name: "coupons");
        }
    }
}
