using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexOrderSummariesForKeysetPagination : Migration
    {
        private static readonly string[] ProjectedAtOrderIdColumns = { "projected_at", "order_id" };
        private static readonly bool[] ProjectedAtOrderIdDescending = { true, true };
        private static readonly string[] CustomerIdProjectedAtOrderIdColumns = { "customer_id", "projected_at", "order_id" };
        private static readonly bool[] CustomerIdProjectedAtOrderIdDescending = { false, true, true };
        private static readonly string[] StatusOrderCreatedAtColumns = { "status", "order_created_at" };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_order_summaries_status",
                table: "order_summaries");

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_customer_id_projected_at_order_id",
                table: "order_summaries",
                columns: CustomerIdProjectedAtOrderIdColumns,
                descending: CustomerIdProjectedAtOrderIdDescending);

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_projected_at_order_id",
                table: "order_summaries",
                columns: ProjectedAtOrderIdColumns,
                descending: ProjectedAtOrderIdDescending);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_order_summaries_customer_id_projected_at_order_id",
                table: "order_summaries");

            migrationBuilder.DropIndex(
                name: "ix_order_summaries_projected_at_order_id",
                table: "order_summaries");

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_status",
                table: "order_summaries",
                columns: StatusOrderCreatedAtColumns);
        }
    }
}
