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
            migrationBuilder.EnsureSchema(
                name: "atel");

            migrationBuilder.CreateTable(
                name: "casos_medicos",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    radicado = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tipo_evento = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fecha_evento = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trabajador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    trabajador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    empleador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    empleador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    municipio_divipola = table.Column<string>(type: "char(5)", nullable: false),
                    estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    es_grave_o_mortal = table.Column<bool>(type: "boolean", nullable: false),
                    calificacion_pcl = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    calificacion_fecha_estructuracion = table.Column<DateOnly>(type: "date", nullable: true),
                    calificacion_origen = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    radicado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_casos_medicos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asignaciones",
                schema: "atel",
                columns: table => new
                {
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sub = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    asignado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    asignado_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asignaciones", x => new { x.caso_medico_id, x.sub });
                    table.ForeignKey(
                        name: "fk_asignaciones_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "remisiones",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ips_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    remitido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    remitido_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    revocada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocada_por = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remisiones", x => x.id);
                    table.ForeignKey(
                        name: "fk_remisiones_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_casos_medicos_tenant_estado_radicado_en",
                schema: "atel",
                table: "casos_medicos",
                columns: new[] { "tenant_id", "estado", "radicado_en" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ux_casos_medicos_tenant_radicado",
                schema: "atel",
                table: "casos_medicos",
                columns: new[] { "tenant_id", "radicado" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remisiones_caso_medico_id",
                schema: "atel",
                table: "remisiones",
                column: "caso_medico_id");

            migrationBuilder.CreateIndex(
                name: "ix_remisiones_ips_vigentes",
                schema: "atel",
                table: "remisiones",
                columns: new[] { "ips_id", "caso_medico_id" },
                filter: "revocada_en IS NULL");

            // ── Permisos de halsa_app ──────────────────────────────────────────
            // Las migraciones corren como halsa_owner (dueño de todo). halsa_app solo recibe
            // lo que la API necesita: sin DELETE sobre casos ni remisiones.
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA app, atel, outbox, event_store TO halsa_app;

                GRANT SELECT, INSERT, UPDATE ON atel.casos_medicos TO halsa_app;
                GRANT SELECT, INSERT, UPDATE ON atel.remisiones    TO halsa_app;
                GRANT SELECT, INSERT, DELETE ON atel.asignaciones  TO halsa_app;

                -- UPDATE también lo exige el SELECT ... FOR UPDATE SKIP LOCKED del OutboxProcessor.
                GRANT SELECT, INSERT, UPDATE ON outbox.outbox_messages TO halsa_app;
                GRANT SELECT, INSERT ON event_store.events TO halsa_app;
                GRANT USAGE ON ALL SEQUENCES IN SCHEMA event_store TO halsa_app;
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // FORCE aplica las políticas también al dueño (halsa_owner): sin app.tenant_id no ve filas.
            // Las políticas no llevan TO, así que cubren a halsa_app y a halsa_owner (seed).
            migrationBuilder.Sql("""
                ALTER TABLE atel.casos_medicos ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.casos_medicos FORCE ROW LEVEL SECURITY;
                ALTER TABLE atel.remisiones    ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.remisiones    FORCE ROW LEVEL SECURITY;
                ALTER TABLE atel.asignaciones  ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.asignaciones  FORCE ROW LEVEL SECURITY;
                """);

            // casos_medicos: la ARL ve y escribe lo suyo; la IPS solo lee lo que tiene remitido.
            migrationBuilder.Sql("""
                CREATE POLICY p_casos_lectura ON atel.casos_medicos FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = casos_medicos.id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                CREATE POLICY p_casos_insercion ON atel.casos_medicos FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_casos_actualizacion ON atel.casos_medicos FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
                """);

            // remisiones: no referencia casos_medicos, así que no hay recursión entre políticas.
            migrationBuilder.Sql("""
                CREATE POLICY p_remisiones_lectura ON atel.remisiones FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR ips_id = current_setting('app.ips_id', true));

                CREATE POLICY p_remisiones_insercion ON atel.remisiones FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_remisiones_actualizacion ON atel.remisiones FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
                """);

            // asignaciones: lectura por tenant o por remisión vigente de la IPS; escritura solo por tenant.
            migrationBuilder.Sql("""
                CREATE POLICY p_asignaciones_lectura ON atel.asignaciones FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = asignaciones.caso_medico_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                CREATE POLICY p_asignaciones_insercion ON atel.asignaciones FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_asignaciones_borrado ON atel.asignaciones FOR DELETE
                    USING (tenant_id = current_setting('app.tenant_id', true));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las políticas caen con sus tablas; solo hay que retirar los permisos fuera de atel.
            migrationBuilder.Sql("""
                REVOKE USAGE ON ALL SEQUENCES IN SCHEMA event_store FROM halsa_app;
                REVOKE SELECT, INSERT ON event_store.events FROM halsa_app;
                REVOKE SELECT, INSERT, UPDATE ON outbox.outbox_messages FROM halsa_app;
                REVOKE USAGE ON SCHEMA app, outbox, event_store FROM halsa_app;
                """);

            migrationBuilder.DropTable(
                name: "asignaciones",
                schema: "atel");

            migrationBuilder.DropTable(
                name: "remisiones",
                schema: "atel");

            migrationBuilder.DropTable(
                name: "casos_medicos",
                schema: "atel");
        }
    }
}
