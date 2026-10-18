using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppleEsportsErp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropMemberHomeBranchForeignKey : Migration
    {
        /// <inheritdoc />
        // A branch's own database holds only its own branch row (BranchAdoptionService wipes the
        // rest), but members are shared and their HomeBranchId names whichever branch they joined
        // at. With this constraint every member from another branch failed to save at every
        // non-home branch - password, balance, everything - since 506eaf64 began writing it.
        // The EF relationship stays (HomeBranch navigation is a LEFT JOIN, null when unknown).
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE members DROP CONSTRAINT IF EXISTS \"FK_members_branches_HomeBranchId\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_members_branches_HomeBranchId",
                table: "members",
                column: "HomeBranchId",
                principalTable: "branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
