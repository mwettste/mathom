using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemCaptureNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaptureNote",
                table: "Items",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CaptureNote",
                table: "Items");
        }
    }
}
