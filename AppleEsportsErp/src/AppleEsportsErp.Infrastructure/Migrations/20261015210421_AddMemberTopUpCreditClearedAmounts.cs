using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberTopUpCreditClearedAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ClearedCashAmount",
                table: "MemberTopUpCredits",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ClearedOnlineAmount",
                table: "MemberTopUpCredits",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClearedCashAmount",
                table: "MemberTopUpCredits");

            migrationBuilder.DropColumn(
                name: "ClearedOnlineAmount",
                table: "MemberTopUpCredits");
        }
    }
}
