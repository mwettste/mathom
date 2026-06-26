using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GlossaryTerms_UserId_Term",
                table: "GlossaryTerms");

            migrationBuilder.AddColumn<Guid>(
                name: "ContextId",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContextId",
                table: "GlossaryTerms",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentContextId",
                table: "AspNetUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contexts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_ContextId",
                table: "Items",
                column: "ContextId");

            migrationBuilder.CreateIndex(
                name: "IX_GlossaryTerms_ContextId",
                table: "GlossaryTerms",
                column: "ContextId");

            migrationBuilder.CreateIndex(
                name: "IX_GlossaryTerms_UserId_ContextId_Term",
                table: "GlossaryTerms",
                columns: new[] { "UserId", "ContextId", "Term" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_CurrentContextId",
                table: "AspNetUsers",
                column: "CurrentContextId");

            migrationBuilder.CreateIndex(
                name: "IX_Contexts_UserId_Name",
                table: "Contexts",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Contexts_CurrentContextId",
                table: "AspNetUsers",
                column: "CurrentContextId",
                principalTable: "Contexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_GlossaryTerms_Contexts_ContextId",
                table: "GlossaryTerms",
                column: "ContextId",
                principalTable: "Contexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Contexts_ContextId",
                table: "Items",
                column: "ContextId",
                principalTable: "Contexts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Contexts_CurrentContextId",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_GlossaryTerms_Contexts_ContextId",
                table: "GlossaryTerms");

            migrationBuilder.DropForeignKey(
                name: "FK_Items_Contexts_ContextId",
                table: "Items");

            migrationBuilder.DropTable(
                name: "Contexts");

            migrationBuilder.DropIndex(
                name: "IX_Items_ContextId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_GlossaryTerms_ContextId",
                table: "GlossaryTerms");

            migrationBuilder.DropIndex(
                name: "IX_GlossaryTerms_UserId_ContextId_Term",
                table: "GlossaryTerms");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_CurrentContextId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ContextId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ContextId",
                table: "GlossaryTerms");

            migrationBuilder.DropColumn(
                name: "CurrentContextId",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_GlossaryTerms_UserId_Term",
                table: "GlossaryTerms",
                columns: new[] { "UserId", "Term" },
                unique: true);
        }
    }
}
