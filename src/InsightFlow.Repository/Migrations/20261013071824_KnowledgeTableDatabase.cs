using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Repository.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeTableDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_knowledge_tables_identity",
                table: "knowledge_tables");

            migrationBuilder.AddColumn<string>(
                name: "database_name",
                table: "knowledge_tables",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_tables_identity",
                table: "knowledge_tables",
                columns: new[] { "tenant_id", "connection_id", "database_name", "schema_name", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_knowledge_tables_identity",
                table: "knowledge_tables");

            migrationBuilder.DropColumn(
                name: "database_name",
                table: "knowledge_tables");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_tables_identity",
                table: "knowledge_tables",
                columns: new[] { "tenant_id", "connection_id", "schema_name", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
