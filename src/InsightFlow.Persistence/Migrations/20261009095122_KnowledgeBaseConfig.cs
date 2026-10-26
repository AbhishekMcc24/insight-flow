using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeBaseConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "knowledge_base_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    user = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    database = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    use_aura = table.Column<bool>(type: "boolean", nullable: false),
                    encrypted = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    password_secret_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_tested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seed_tables = table.Column<int>(type: "integer", nullable: false),
                    last_seed_columns = table.Column<int>(type: "integer", nullable: false),
                    last_seed_relationships = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_knowledge_base_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_knowledge_base_configs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_knowledge_base_configs_tenant",
                table: "knowledge_base_configs",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "knowledge_base_configs");
        }
    }
}
