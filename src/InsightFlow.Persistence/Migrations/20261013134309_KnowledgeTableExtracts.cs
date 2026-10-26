using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeTableExtracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "extract_rows",
                table: "knowledge_tables",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "extract_truncated",
                table: "knowledge_tables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "extract_version_id",
                table: "knowledge_tables",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "extracted_at",
                table: "knowledge_tables",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extract_rows",
                table: "knowledge_tables");

            migrationBuilder.DropColumn(
                name: "extract_truncated",
                table: "knowledge_tables");

            migrationBuilder.DropColumn(
                name: "extract_version_id",
                table: "knowledge_tables");

            migrationBuilder.DropColumn(
                name: "extracted_at",
                table: "knowledge_tables");
        }
    }
}
