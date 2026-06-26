using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Mathom.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmbeddedAt",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Vector>(
                name: "Embedding",
                table: "Items",
                type: "vector(1024)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                @"CREATE INDEX ""IX_Items_Embedding"" ON ""Items"" USING hnsw (""Embedding"" vector_cosine_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Items_Embedding"";");

            migrationBuilder.DropColumn(
                name: "EmbeddedAt",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "Items");
        }
    }
}
