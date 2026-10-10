using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "event_store");

            migrationBuilder.EnsureSchema(
                name: "outbox");

            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "events",
                schema: "event_store",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    stream_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    recorded_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.sequence);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "work_items",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_stream_id_version",
                schema: "event_store",
                table: "events",
                columns: new[] { "stream_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_stream_type",
                schema: "event_store",
                table: "events",
                column: "stream_type");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unprocessed",
                schema: "outbox",
                table: "outbox_messages",
                column: "occurred_on_utc",
                filter: "processed_on_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_work_items_status",
                schema: "app",
                table: "work_items",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events",
                schema: "event_store");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "outbox");

            migrationBuilder.DropTable(
                name: "work_items",
                schema: "app");
        }
    }
}
