using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_tables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schema_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_tables", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_tables_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_columns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    data_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_primary_key = table.Column<bool>(type: "boolean", nullable: false),
                    is_nullable = table.Column<bool>(type: "boolean", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_columns", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_columns_knowledge_tables_table_id",
                        column: x => x.table_id,
                        principalTable: "knowledge_tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_columns_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_relationships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_column_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_column_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_relationships", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_relationships_knowledge_columns_from_column_id",
                        column: x => x.from_column_id,
                        principalTable: "knowledge_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_relationships_knowledge_columns_to_column_id",
                        column: x => x.to_column_id,
                        principalTable: "knowledge_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_knowledge_relationships_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_columns_tenant_id",
                table: "knowledge_columns",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_columns_name",
                table: "knowledge_columns",
                columns: new[] { "table_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_relationships_tenant_id",
                table: "knowledge_relationships",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_knowledge_relationships_to_column_id",
                table: "knowledge_relationships",
                column: "to_column_id");

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_relationships_pair",
                table: "knowledge_relationships",
                columns: new[] { "from_column_id", "to_column_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_tables_identity",
                table: "knowledge_tables",
                columns: new[] { "tenant_id", "connection_id", "schema_name", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_relationships");

            migrationBuilder.DropTable(
                name: "knowledge_columns");

            migrationBuilder.DropTable(
                name: "knowledge_tables");
        }
    }
}
