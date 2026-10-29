using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InsightFlow.Repository.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeRelationshipGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "group_id",
                table: "knowledge_relationships",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_relationships_group",
                table: "knowledge_relationships",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_knowledge_relationships_group",
                table: "knowledge_relationships");

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "knowledge_relationships");
        }
    }
}
