using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlossaryTermDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "GlossaryTerms",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "GlossaryTerms");
        }
    }
}
