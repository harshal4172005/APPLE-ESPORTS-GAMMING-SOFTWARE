using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedStockAtHeadOffice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "shared_stock_version_seq");

            migrationBuilder.AddColumn<bool>(
                name: "StockTaken",
                table: "food_orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OfflineSellerBranchId",
                table: "food_groups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shared_stock_counts",
                columns: table => new
                {
                    FoodGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_stock_counts", x => new { x.FoodGroupId, x.InventoryItemId });
                });

            migrationBuilder.CreateTable(
                name: "shared_stock_pending",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Op = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_stock_pending", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shared_stock_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FoodGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Op = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ResultCount = table.Column<int>(type: "integer", nullable: true),
                    RefusedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_stock_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shared_stock_counts_FoodGroupId_Version",
                table: "shared_stock_counts",
                columns: new[] { "FoodGroupId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_stock_requests_CreatedAt",
                table: "shared_stock_requests",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shared_stock_counts");

            migrationBuilder.DropTable(
                name: "shared_stock_pending");

            migrationBuilder.DropTable(
                name: "shared_stock_requests");

            migrationBuilder.DropColumn(
                name: "StockTaken",
                table: "food_orders");

            migrationBuilder.DropColumn(
                name: "OfflineSellerBranchId",
                table: "food_groups");

            migrationBuilder.DropSequence(
                name: "shared_stock_version_seq");
        }
    }
}
