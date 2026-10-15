using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 12: las solicitudes de copia de la HC. RLS para la ARL del caso (la bandeja) y para el paciente que la crea,
    /// solo sobre un caso que ve; y la regla <c>SOLICITUD_COPIA_HC</c> del motor de términos.
    /// </summary>
    public partial class SolicitudesCopiaHc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "solicitudes_copia_hc",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paciente_sub = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    medio_entrega = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    direccion_o_correo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    observaciones = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    solicitada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    estado = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    atendida_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    atendida_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    nota_atencion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_solicitudes_copia_hc", x => x.id);
                    table.ForeignKey(
                        name: "fk_solicitudes_copia_hc_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_solicitudes_copia_hc_paciente_solicitada_en",
                schema: "atel",
                table: "solicitudes_copia_hc",
                columns: new[] { "paciente_sub", "solicitada_en" });

            migrationBuilder.CreateIndex(
                name: "ix_solicitudes_copia_hc_tenant_estado_solicitada_en",
                schema: "atel",
                table: "solicitudes_copia_hc",
                columns: new[] { "tenant_id", "estado", "solicitada_en" });

            migrationBuilder.CreateIndex(
                name: "ux_solicitudes_copia_hc_caso_abierta",
                schema: "atel",
                table: "solicitudes_copia_hc",
                column: "caso_medico_id",
                unique: true,
                filter: "estado IN ('RECIBIDA', 'EN_TRAMITE')");

            // ── Restricciones que EF no expresa ────────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE atel.solicitudes_copia_hc ADD CONSTRAINT ck_solicitudes_copia_hc_valores CHECK (
                    medio_entrega IN ('CORREO_CERTIFICADO', 'PRESENCIAL', 'ELECTRONICO')
                    AND estado IN ('RECIBIDA', 'EN_TRAMITE', 'ENTREGADA', 'RECHAZADA')
                    AND (medio_entrega = 'PRESENCIAL' OR direccion_o_correo IS NOT NULL)
                    AND (estado NOT IN ('ENTREGADA', 'RECHAZADA')
                         OR (atendida_en IS NOT NULL AND atendida_por IS NOT NULL
                             AND char_length(nota_atencion) >= 10)));
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // The ARL of the case reads and works its inbox. The patient reads its own and creates them only on a case it
            // sees (the RLS of casos_medicos is the link), pinned to that case's tenant.
            migrationBuilder.Sql("""
                ALTER TABLE atel.solicitudes_copia_hc ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.solicitudes_copia_hc FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_solicitudes_copia_hc_lectura ON atel.solicitudes_copia_hc FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR (current_setting('app.actor_tipo', true) = 'paciente'
                        AND paciente_sub = current_setting('app.sub', true)));

                CREATE POLICY p_solicitudes_copia_hc_insercion ON atel.solicitudes_copia_hc FOR INSERT WITH CHECK (
                    current_setting('app.actor_tipo', true) = 'paciente'
                    AND paciente_sub = current_setting('app.sub', true)
                    AND EXISTS (SELECT 1 FROM atel.casos_medicos c
                                WHERE c.id = solicitudes_copia_hc.caso_medico_id
                                  AND c.tenant_id = solicitudes_copia_hc.tenant_id));

                CREATE POLICY p_solicitudes_copia_hc_actualizacion ON atel.solicitudes_copia_hc FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                GRANT SELECT, INSERT, UPDATE ON atel.solicitudes_copia_hc TO halsa_app;
                """);

            // ── La regla del plazo ─────────────────────────────────────────────
            // PENDIENTE DE VALIDACIÓN JURÍDICA antes de producción, como las semillas de ARQ §3.4.
            migrationBuilder.Sql("""
                INSERT INTO sst.reglas_termino
                    (id, codigo, evento_disparador, cantidad, unidad, alerta_cantidad, alerta_unidad, base_legal, vigente_desde)
                VALUES
                    ('01992a00-0000-7000-8000-000000000007', 'SOLICITUD_COPIA_HC', 'SolicitudCopiaHcCreada', 10, 'HABILES',
                     3, 'HABILES', 'Ley 1581/2012, art. 14; Res. 1995/1999, art. 14', DATE '2000-01-01');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails while a SOLICITUD_COPIA_HC plazo exists: the plazos keep their rule (fk_plazos_reglas_termino).
            migrationBuilder.Sql("DELETE FROM sst.reglas_termino WHERE id = '01992a00-0000-7000-8000-000000000007';");

            migrationBuilder.DropTable(
                name: "solicitudes_copia_hc",
                schema: "atel");
        }
    }
}
