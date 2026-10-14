using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 08: <c>atel.dictamenes_pcl</c>, la parte administrativa del dictamen PCL. Un solo dictamen vigente por caso
    /// (índice único parcial). <c>halsa_ingesta</c> lo registra junto con la calificación del caso, con <c>UPDATE</c>
    /// limitado por columnas; <c>halsa_app</c> lo lee y registra su notificación.
    /// </summary>
    public partial class AtelDictamenesPcl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dictamenes_pcl",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registro_clinico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    porcentaje_pcl = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    origen = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    fecha_estructuracion = table.Column<DateOnly>(type: "date", nullable: false),
                    fecha_dictamen = table.Column<DateOnly>(type: "date", nullable: false),
                    calificador_sub = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    calificador_nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    calificador_registro_rethus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    estado_notificacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notificado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    medio_notificacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    corrige_a = table.Column<Guid>(type: "uuid", nullable: true),
                    corregido_por = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dictamenes_pcl", x => x.id);
                    table.ForeignKey(
                        name: "fk_dictamenes_pcl_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_dictamenes_pcl_caso_vigente",
                schema: "atel",
                table: "dictamenes_pcl",
                column: "caso_medico_id",
                unique: true,
                filter: "corregido_por IS NULL");

            // ── Restricciones que EF no expresa ────────────────────────────────
            // corregido_por points to the new dictamen, which is inserted after the old one stops being live (else the
            // partial unique index would see two): its FK is checked at commit.
            migrationBuilder.Sql("""
                ALTER TABLE atel.dictamenes_pcl ADD CONSTRAINT fk_dictamenes_pcl_corrige_a
                    FOREIGN KEY (corrige_a) REFERENCES atel.dictamenes_pcl (id) ON DELETE RESTRICT;

                ALTER TABLE atel.dictamenes_pcl ADD CONSTRAINT fk_dictamenes_pcl_corregido_por
                    FOREIGN KEY (corregido_por) REFERENCES atel.dictamenes_pcl (id) ON DELETE RESTRICT
                    DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE atel.dictamenes_pcl ADD CONSTRAINT ck_dictamenes_pcl_porcentaje
                    CHECK (porcentaje_pcl BETWEEN 0 AND 100);

                ALTER TABLE atel.dictamenes_pcl ADD CONSTRAINT ck_dictamenes_pcl_fechas
                    CHECK (fecha_estructuracion <= fecha_dictamen);

                ALTER TABLE atel.dictamenes_pcl ADD CONSTRAINT ck_dictamenes_pcl_codigos CHECK (
                    origen IN ('LABORAL', 'COMUN', 'MIXTO')
                    AND estado_notificacion IN ('PENDIENTE', 'NOTIFICADO', 'EN_CONTROVERSIA', 'EN_FIRME')
                    AND (medio_notificacion IS NULL
                         OR medio_notificacion IN ('CORREO_CERTIFICADO', 'ELECTRONICO', 'PERSONAL')));
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // Read as casos_medicos (the tenant, or a case remitted and live to the IPS). Written only by the tenant:
            // the dictamen is the ARL's.
            migrationBuilder.Sql("""
                ALTER TABLE atel.dictamenes_pcl ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.dictamenes_pcl FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_dictamenes_pcl_lectura ON atel.dictamenes_pcl FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = dictamenes_pcl.caso_medico_id
                                 AND r.tenant_id = dictamenes_pcl.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                CREATE POLICY p_dictamenes_pcl_insercion ON atel.dictamenes_pcl FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_dictamenes_pcl_actualizacion ON atel.dictamenes_pcl FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            // halsa_ingesta: the dictamen, its correction, and only the case's state and calificación.
            // halsa_app: reads it and records its notification (an administrative fact).
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON atel.dictamenes_pcl TO halsa_ingesta;
                GRANT UPDATE (corregido_por) ON atel.dictamenes_pcl TO halsa_ingesta;
                GRANT UPDATE (estado, calificacion_pcl, calificacion_fecha_estructuracion, calificacion_origen)
                    ON atel.casos_medicos TO halsa_ingesta;

                GRANT SELECT ON atel.dictamenes_pcl TO halsa_app;
                GRANT UPDATE (estado_notificacion, notificado_en, medio_notificacion) ON atel.dictamenes_pcl TO halsa_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies, the SQL constraints and the table's grants fall with the table; the case's do not.
            migrationBuilder.Sql("""
                REVOKE UPDATE (estado, calificacion_pcl, calificacion_fecha_estructuracion, calificacion_origen)
                    ON atel.casos_medicos FROM halsa_ingesta;
                """);

            migrationBuilder.DropTable(
                name: "dictamenes_pcl",
                schema: "atel");
        }
    }
}
