using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpreadsheetIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_ingest_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    sheet_count = table.Column<int>(type: "integer", nullable: false),
                    row_count = table.Column<long>(type: "bigint", nullable: false),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    problems = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_ingest_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_document_ingest_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "indexed_sheets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    sheet_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    table_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rows = table.Column<long>(type: "bigint", nullable: false),
                    columns = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_indexed_sheets", x => x.id);
                    table.ForeignKey(
                        name: "FK_indexed_sheets_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_ingest_runs_status",
                table: "document_ingest_runs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_document_ingest_runs_tenant_requested",
                table: "document_ingest_runs",
                columns: new[] { "tenant_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_indexed_sheets_item",
                table: "indexed_sheets",
                columns: new[] { "tenant_id", "content_item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_ingest_runs");

            migrationBuilder.DropTable(
                name: "indexed_sheets");
        }
    }
}
