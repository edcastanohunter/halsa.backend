using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 07: <c>atel.incapacidades</c>, la parte administrativa de una incapacidad. La misma RLS que
    /// <c>casos_medicos</c> (el tenant o una remisión vigente del caso), también para insertar, porque la integración
    /// de una IPS carga incapacidades de casos de varias ARL. <c>halsa_app</c> lee; <c>halsa_ingesta</c> lee e
    /// inserta. Nadie actualiza ni borra.
    /// </summary>
    public partial class AtelIncapacidades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incapacidades",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_certificado = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ips_emisora_reps = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ips_emisora_nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    prorroga_de = table.Column<Guid>(type: "uuid", nullable: true),
                    cadena_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fecha_inicio = table.Column<DateOnly>(type: "date", nullable: false),
                    dias = table.Column<int>(type: "integer", nullable: false),
                    fecha_fin = table.Column<DateOnly>(type: "date", nullable: false),
                    dias_acumulados = table.Column<int>(type: "integer", nullable: false),
                    modalidad = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    estado = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    profesional_tipo_documento = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    profesional_numero_documento = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    profesional_nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    profesional_registro_rethus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    diagnostico_categoria_indice = table.Column<byte[]>(type: "bytea", nullable: false),
                    soporte_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registro_clinico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registrada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registrada_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incapacidades", x => x.id);
                    table.ForeignKey(
                        name: "fk_incapacidades_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incapacidades_prorroga_de",
                        column: x => x.prorroga_de,
                        principalSchema: "atel",
                        principalTable: "incapacidades",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incapacidades_caso_medico_id",
                schema: "atel",
                table: "incapacidades",
                column: "caso_medico_id");

            migrationBuilder.CreateIndex(
                name: "ix_incapacidades_prorroga_de",
                schema: "atel",
                table: "incapacidades",
                column: "prorroga_de");

            migrationBuilder.CreateIndex(
                name: "ix_incapacidades_tenant_caso_fecha_inicio",
                schema: "atel",
                table: "incapacidades",
                columns: new[] { "tenant_id", "caso_medico_id", "fecha_inicio" });

            // ── Restricciones que EF no expresa ────────────────────────────────
            // The certificate key mixes the owner's columns with an owned type's. cadena_id points to the initial
            // of the chain, which for an initial is the row itself: an FK EF would see as a cycle.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_incapacidades_tenant_ips_certificado
                    ON atel.incapacidades (tenant_id, ips_emisora_reps, numero_certificado);

                ALTER TABLE atel.incapacidades ADD CONSTRAINT fk_incapacidades_cadena
                    FOREIGN KEY (cadena_id) REFERENCES atel.incapacidades (id) ON DELETE RESTRICT;

                ALTER TABLE atel.incapacidades ADD CONSTRAINT ck_incapacidades_dias
                    CHECK (dias BETWEEN 1 AND 30 AND fecha_fin = fecha_inicio + (dias - 1));
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // As casos_medicos: the ARL sees its own; the IPS sees those of a case it has remitted and live. Also
            // for INSERT, with the remission's tenant pinned, so an IPS cannot file under another ARL.
            migrationBuilder.Sql("""
                ALTER TABLE atel.incapacidades ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.incapacidades FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_incapacidades_lectura ON atel.incapacidades FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = incapacidades.caso_medico_id
                                 AND r.tenant_id = incapacidades.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                CREATE POLICY p_incapacidades_insercion ON atel.incapacidades FOR INSERT WITH CHECK (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = incapacidades.caso_medico_id
                                 AND r.tenant_id = incapacidades.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                GRANT SELECT ON atel.incapacidades TO halsa_app;
                GRANT SELECT, INSERT ON atel.incapacidades TO halsa_ingesta;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies, the SQL constraints and the grants fall with the table.
            migrationBuilder.DropTable(
                name: "incapacidades",
                schema: "atel");
        }
    }
}
