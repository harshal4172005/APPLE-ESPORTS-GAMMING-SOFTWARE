using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberTopUpCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemberTopUpCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClearedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClearedByOperatorId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberTopUpCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberTopUpCredits_branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemberTopUpCredits_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemberTopUpCredits_operators_ClearedByOperatorId",
                        column: x => x.ClearedByOperatorId,
                        principalTable: "operators",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MemberTopUpCredits_operators_OperatorId",
                        column: x => x.OperatorId,
                        principalTable: "operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MemberTopUpCredits_BranchId",
                table: "MemberTopUpCredits",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberTopUpCredits_ClearedByOperatorId",
                table: "MemberTopUpCredits",
                column: "ClearedByOperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberTopUpCredits_MemberId",
                table: "MemberTopUpCredits",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberTopUpCredits_OperatorId",
                table: "MemberTopUpCredits",
                column: "OperatorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberTopUpCredits");
        }
    }
}
