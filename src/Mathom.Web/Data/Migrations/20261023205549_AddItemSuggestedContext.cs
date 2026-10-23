using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemSuggestedContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SuggestedContextId",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_SuggestedContextId",
                table: "Items",
                column: "SuggestedContextId");

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Contexts_SuggestedContextId",
                table: "Items",
                column: "SuggestedContextId",
                principalTable: "Contexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Items_Contexts_SuggestedContextId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_SuggestedContextId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SuggestedContextId",
                table: "Items");
        }
    }
}
