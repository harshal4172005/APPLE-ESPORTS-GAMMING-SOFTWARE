using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrentSessionPausedSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // OpenedAt, CountedAt, CountedByBranchId, CountedByName, PlayMinutes
            // were already added by DbUpdater on live servers — use IF NOT EXISTS
            // so the migration is safe on both fresh and existing databases.
            migrationBuilder.Sql("""
                ALTER TABLE stock_counts ADD COLUMN IF NOT EXISTS "OpenedAt" timestamptz;
                ALTER TABLE shared_stock_counts ADD COLUMN IF NOT EXISTS "CountedAt" timestamptz;
                ALTER TABLE shared_stock_counts ADD COLUMN IF NOT EXISTS "CountedByBranchId" uuid;
                ALTER TABLE shared_stock_counts ADD COLUMN IF NOT EXISTS "CountedByName" text;
                ALTER TABLE bills ADD COLUMN IF NOT EXISTS "PlayMinutes" integer;
                """);

            migrationBuilder.AddColumn<int>(
                name: "CurrentSessionPausedSeconds",
                table: "pcs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenedAt",
                table: "stock_counts");

            migrationBuilder.DropColumn(
                name: "CountedAt",
                table: "shared_stock_counts");

            migrationBuilder.DropColumn(
                name: "CountedByBranchId",
                table: "shared_stock_counts");

            migrationBuilder.DropColumn(
                name: "CountedByName",
                table: "shared_stock_counts");

            migrationBuilder.DropColumn(
                name: "CurrentSessionPausedSeconds",
                table: "pcs");

            migrationBuilder.DropColumn(
                name: "PlayMinutes",
                table: "bills");
        }
    }
}
