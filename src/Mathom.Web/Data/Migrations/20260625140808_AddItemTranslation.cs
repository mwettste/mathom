using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemTranslation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "Items",
                type: "tsvector",
                nullable: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldNullable: true)
                .Annotation("Npgsql:TsVectorConfig", "simple")
                .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "CleanText" })
                .OldAnnotation("Npgsql:TsVectorConfig", "english")
                .OldAnnotation("Npgsql:TsVectorProperties", new[] { "Title", "CleanText" });

            migrationBuilder.AddColumn<string>(
                name: "SourceLanguage",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    CleanText = table.Column<string>(type: "text", nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true)
                        .Annotation("Npgsql:TsVectorConfig", "simple")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "CleanText" })
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemTranslations_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ItemTranslations_ItemId_Locale",
                table: "ItemTranslations",
                columns: new[] { "ItemId", "Locale" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemTranslations_SearchVector",
                table: "ItemTranslations",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemTranslations");

            migrationBuilder.DropColumn(
                name: "SourceLanguage",
                table: "Items");

            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "Items",
                type: "tsvector",
                nullable: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldNullable: true)
                .Annotation("Npgsql:TsVectorConfig", "english")
                .Annotation("Npgsql:TsVectorProperties", new[] { "Title", "CleanText" })
                .OldAnnotation("Npgsql:TsVectorConfig", "simple")
                .OldAnnotation("Npgsql:TsVectorProperties", new[] { "Title", "CleanText" });
        }
    }
}
