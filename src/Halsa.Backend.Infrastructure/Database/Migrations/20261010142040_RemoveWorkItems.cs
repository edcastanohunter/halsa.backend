using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_work_items_status",
                schema: "app",
                table: "work_items");

            migrationBuilder.DropTable(
                name: "work_items",
                schema: "app");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_items",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_on_utc = table.Column<System.DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_on_utc = table.Column<System.DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_items_status",
                schema: "app",
                table: "work_items",
                column: "status");
        }
    }
}
