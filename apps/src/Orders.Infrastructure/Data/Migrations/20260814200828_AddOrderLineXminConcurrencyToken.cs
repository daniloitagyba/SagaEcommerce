using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <summary>
    /// No DDL: xmin is a Postgres system column, already present on every
    /// table since it was created - "ADD COLUMN xmin" is not legal DDL
    /// (Postgres reserves the name) and was never run. This migration exists
    /// only so the EF model snapshot picks up OrdersDbContext's new mapping
    /// of that existing column as OrderLine's concurrency token (see
    /// ConfigureOrderLine) - scaffolding generated the AddColumn EF Core
    /// always emits for a newly-mapped property, which has been replaced
    /// with a no-op deliberately.
    /// </summary>
    public partial class AddOrderLineXminConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
