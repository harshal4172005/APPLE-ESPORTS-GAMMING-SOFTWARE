using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPackagePriceFromMin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PackageFromMin",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PackageMinutes",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PackagePriceFromMin",
                table: "PricingPackages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentSessionPackageFromMin",
                table: "pcs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PackageFromMin",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PackageMinutes",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PackagePriceFromMin",
                table: "PricingPackages");

            migrationBuilder.DropColumn(
                name: "CurrentSessionPackageFromMin",
                table: "pcs");
        }
    }
}
