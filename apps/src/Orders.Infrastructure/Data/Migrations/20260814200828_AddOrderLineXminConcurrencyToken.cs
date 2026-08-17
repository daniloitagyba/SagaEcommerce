using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orders.Infrastructure.Data.Migrations
{
    /// <summary>No-op migration: xmin is a pre-existing Postgres system column, so this exists only to update the EF model snapshot for OrderLine's new concurrency-token mapping.</summary>
    public partial class AddOrderLineXminConcurrencyToken : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
