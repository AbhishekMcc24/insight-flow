using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Repository.Migrations
{
    /// <inheritdoc />
    public partial class QueryFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "query_feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    question = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    sql = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    is_right = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_query_feedback", x => x.id);
                    table.ForeignKey(
                        name: "FK_query_feedback_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_query_feedback_tenant_created",
                table: "query_feedback",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "query_feedback");
        }
    }
}
