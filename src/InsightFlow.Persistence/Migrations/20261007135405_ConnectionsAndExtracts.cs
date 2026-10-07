using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConnectionsAndExtracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    secret = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_connection_profiles_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "extract_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_table = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    latest_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extract_definitions", x => x.id);
                    table.CheckConstraint("ck_extract_definitions_one_source", "(connection_profile_id IS NOT NULL AND stored_file_id IS NULL) OR (connection_profile_id IS NULL AND stored_file_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_extract_definitions_connection_profiles_connection_profile_~",
                        column: x => x.connection_profile_id,
                        principalTable: "connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extract_definitions_dataset_versions_latest_version_id",
                        column: x => x.latest_version_id,
                        principalTable: "dataset_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extract_definitions_folders_target_folder_id",
                        column: x => x.target_folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extract_definitions_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extract_definitions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "extract_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    row_count = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extract_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_extract_runs_extract_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "extract_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extract_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_profiles_tenant_id_name",
                table: "connection_profiles",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "IX_extract_definitions_connection_profile_id",
                table: "extract_definitions",
                column: "connection_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_definitions_latest_version_id",
                table: "extract_definitions",
                column: "latest_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_definitions_stored_file_id",
                table: "extract_definitions",
                column: "stored_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_definitions_target_folder_id",
                table: "extract_definitions",
                column: "target_folder_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_definitions_tenant_id",
                table: "extract_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_runs_definition_id",
                table: "extract_runs",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_extract_runs_status_requested_at",
                table: "extract_runs",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_extract_runs_tenant_id_definition_id_requested_at",
                table: "extract_runs",
                columns: new[] { "tenant_id", "definition_id", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extract_runs");

            migrationBuilder.DropTable(
                name: "extract_definitions");

            migrationBuilder.DropTable(
                name: "connection_profiles");
        }
    }
}
