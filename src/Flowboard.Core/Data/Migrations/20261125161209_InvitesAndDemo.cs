using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowboard.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvitesAndDemo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InviteCode",
                table: "Workspaces",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_InviteCode",
                table: "Workspaces",
                column: "InviteCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workspaces_InviteCode",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "InviteCode",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Workspaces");
        }
    }
}
