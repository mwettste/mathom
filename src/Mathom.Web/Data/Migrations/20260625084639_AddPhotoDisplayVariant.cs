using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoDisplayVariant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "ItemPhotos",
                type: "text",
                nullable: true); // nullable first so existing rows can be backfilled before the unique index

            migrationBuilder.AddColumn<string>(
                name: "DisplayPath",
                table: "ItemPhotos",
                type: "text",
                nullable: true);

            // New rows get nanoids from the app; backfill any pre-existing rows with unique values.
            migrationBuilder.Sql(
                "UPDATE \"ItemPhotos\" SET \"ExternalId\" = gen_random_uuid()::text WHERE \"ExternalId\" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalId",
                table: "ItemPhotos",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotos_ExternalId",
                table: "ItemPhotos",
                column: "ExternalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_ItemPhotos_ExternalId", table: "ItemPhotos");
            migrationBuilder.DropColumn(name: "DisplayPath", table: "ItemPhotos");
            migrationBuilder.DropColumn(name: "ExternalId", table: "ItemPhotos");
        }
    }
}
