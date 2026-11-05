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
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OpenedAt",
                table: "stock_counts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CountedAt",
                table: "shared_stock_counts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CountedByBranchId",
                table: "shared_stock_counts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountedByName",
                table: "shared_stock_counts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentSessionPausedSeconds",
                table: "pcs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PlayMinutes",
                table: "bills",
                type: "integer",
                nullable: true);
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
