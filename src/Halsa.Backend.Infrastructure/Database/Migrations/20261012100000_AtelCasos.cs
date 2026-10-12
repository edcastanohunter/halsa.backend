using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AtelCasos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Schema ─────────────────────────────────────────────────────────
            migrationBuilder.EnsureSchema(name: "atel");

            // ── caso_medico ────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "caso_medico",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    radicado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tipo_evento = table.Column<int>(type: "integer", nullable: false),
                    fecha_evento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trabajador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    trabajador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    empleador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    empleador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    municipio_divipola = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    es_grave_o_mortal = table.Column<bool>(type: "boolean", nullable: false),
                    calificacion_pcl = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    calificacion_fecha_estructuracion = table.Column<DateOnly>(type: "date", nullable: true),
                    calificacion_origen = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    radicado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_caso_medico", x => x.id);
                });

            // ── remision ───────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "remision",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ips_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    remitido_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    remitido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revocada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocada_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remision", x => x.id);
                    table.ForeignKey(
                        name: "fk_remision_caso_medico",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "caso_medico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ── asignacion ─────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "asignacion",
                schema: "atel",
                columns: table => new
                {
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asignado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    asignado_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asignacion", x => new { x.caso_medico_id, x.sub });
                    table.ForeignKey(
                        name: "fk_asignacion_caso_medico",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "caso_medico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ── Indexes ────────────────────────────────────────────────────────
            migrationBuilder.CreateIndex(
                name: "ix_caso_medico_tenant_id",
                schema: "atel",
                table: "caso_medico",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_caso_medico_tenant_estado",
                schema: "atel",
                table: "caso_medico",
                columns: new[] { "tenant_id", "estado" });

            migrationBuilder.CreateIndex(
                name: "ix_remision_caso_medico_id",
                schema: "atel",
                table: "remision",
                column: "caso_medico_id");

            migrationBuilder.CreateIndex(
                name: "ix_remision_ips_id",
                schema: "atel",
                table: "remision",
                column: "ips_id");

            // ── Grants to halsa_app (runtime role) ────────────────────────────
            // halsa_owner runs migrations; halsa_app owns the application runtime.
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA atel TO halsa_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON atel.caso_medico TO halsa_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON atel.remision TO halsa_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON atel.asignacion TO halsa_app;");

            // ── Row-Level Security on caso_medico ─────────────────────────────
            // FORCE RLS applies to the table owner (halsa_owner) as well, protecting
            // migrations from accidentally reading cross-tenant data.
            migrationBuilder.Sql("ALTER TABLE atel.caso_medico ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE atel.caso_medico FORCE ROW LEVEL SECURITY;");

            // ARL actors: see cases belonging to their tenant.
            migrationBuilder.Sql("""
                CREATE POLICY pol_caso_medico_arl
                ON atel.caso_medico
                FOR ALL
                TO halsa_app
                USING (tenant_id = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
                """);

            // IPS actors: see cases that have a vigente remisión to their IPS.
            migrationBuilder.Sql("""
                CREATE POLICY pol_caso_medico_ips
                ON atel.caso_medico
                FOR SELECT
                TO halsa_app
                USING (
                    EXISTS (
                        SELECT 1
                        FROM atel.remision r
                        WHERE r.caso_medico_id = caso_medico.id
                          AND r.ips_id = current_setting('app.ips_id', true)
                          AND r.revocada_en IS NULL
                    )
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revoke RLS first, then drop tables.
            migrationBuilder.Sql("DROP POLICY IF EXISTS pol_caso_medico_ips ON atel.caso_medico;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS pol_caso_medico_arl ON atel.caso_medico;");
            migrationBuilder.Sql("ALTER TABLE atel.caso_medico DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(name: "asignacion", schema: "atel");
            migrationBuilder.DropTable(name: "remision", schema: "atel");
            migrationBuilder.DropTable(name: "caso_medico", schema: "atel");
        }
    }
}
