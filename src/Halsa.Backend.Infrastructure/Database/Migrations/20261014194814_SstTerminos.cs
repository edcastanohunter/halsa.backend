using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 09: el esquema <c>sst</c> del motor de términos. <c>reglas_termino</c> y <c>festivos</c> son globales;
    /// <c>plazos</c> tiene RLS por tenant para <c>halsa_app</c> y una política propia para <c>halsa_worker</c>, que los
    /// barre entre tenants. <c>halsa_worker</c> recibe además lo justo para los jobs de mantenimiento (la idempotencia
    /// vencida, el outbox y las particiones de auditoría), y nunca <c>USAGE</c> sobre <c>atel</c>. Su acceso a
    /// <c>clinico.soportes</c> lo da la migración clínica <c>SoportesWorker</c>, que corre después.
    /// </summary>
    public partial class SstTerminos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sst");

            migrationBuilder.CreateTable(
                name: "festivos",
                schema: "sst",
                columns: table => new
                {
                    fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    descripcion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    origen = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_festivos", x => x.fecha);
                });

            migrationBuilder.CreateTable(
                name: "reglas_termino",
                schema: "sst",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    evento_disparador = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    cantidad = table.Column<int>(type: "integer", nullable: false),
                    unidad = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    alerta_cantidad = table.Column<int>(type: "integer", nullable: false),
                    alerta_unidad = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    base_legal = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    vigente_desde = table.Column<DateOnly>(type: "date", nullable: false),
                    vigente_hasta = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reglas_termino", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plazos",
                schema: "sst",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    regla_id = table.Column<Guid>(type: "uuid", nullable: false),
                    regla_codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    referencia_id = table.Column<Guid>(type: "uuid", nullable: true),
                    radicado = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    inicia_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vence_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    alerta_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    estado = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    alertado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cumplido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cumplido_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    nota_cumplimiento = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    extemporaneo = table.Column<bool>(type: "boolean", nullable: false),
                    ultima_fecha_fin = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plazos", x => x.id);
                    table.ForeignKey(
                        name: "fk_plazos_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_plazos_reglas_termino",
                        column: x => x.regla_id,
                        principalSchema: "sst",
                        principalTable: "reglas_termino",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plazos_abiertos_vence_en",
                schema: "sst",
                table: "plazos",
                column: "vence_en",
                filter: "estado = 'ABIERTO'");

            migrationBuilder.CreateIndex(
                name: "ix_plazos_regla_id",
                schema: "sst",
                table: "plazos",
                column: "regla_id");

            migrationBuilder.CreateIndex(
                name: "ix_plazos_tenant_estado_vence_en",
                schema: "sst",
                table: "plazos",
                columns: new[] { "tenant_id", "estado", "vence_en" });

            migrationBuilder.CreateIndex(
                name: "ux_plazos_caso_regla_referencia",
                schema: "sst",
                table: "plazos",
                columns: new[] { "caso_medico_id", "regla_codigo", "referencia_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ux_reglas_termino_codigo_vigente_desde",
                schema: "sst",
                table: "reglas_termino",
                columns: new[] { "codigo", "vigente_desde" },
                unique: true);

            // ── Restricciones que EF no expresa ────────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE sst.reglas_termino ADD CONSTRAINT ck_reglas_termino_valores CHECK (
                    unidad IN ('HABILES', 'CALENDARIO', 'HORAS')
                    AND alerta_unidad IN ('HABILES', 'CALENDARIO', 'HORAS')
                    AND cantidad > 0
                    AND alerta_cantidad >= 0
                    AND (vigente_hasta IS NULL OR vigente_hasta >= vigente_desde));

                ALTER TABLE sst.festivos ADD CONSTRAINT ck_festivos_origen CHECK (origen IN ('CALCULADO', 'MANUAL'));

                ALTER TABLE sst.plazos ADD CONSTRAINT ck_plazos_estado
                    CHECK (estado IN ('ABIERTO', 'CUMPLIDO', 'VENCIDO', 'SUSPENDIDO'));

                ALTER TABLE sst.plazos ADD CONSTRAINT ck_plazos_cumplimiento CHECK (
                    (estado <> 'CUMPLIDO' OR cumplido_en IS NOT NULL)
                    AND (nota_cumplimiento IS NULL OR char_length(nota_cumplimiento) >= 10));
                """);

            // ── Reglas semilla ─────────────────────────────────────────────────
            // ARQ §3.4. PENDIENTES DE VALIDACIÓN JURÍDICA antes de producción. Vigentes desde 2000 para que todo evento
            // en el sistema encuentre su versión; un cambio es una fila nueva con vigente_desde, nunca un UPDATE.
            migrationBuilder.Sql("""
                INSERT INTO sst.reglas_termino
                    (id, codigo, evento_disparador, cantidad, unidad, alerta_cantidad, alerta_unidad, base_legal, vigente_desde)
                VALUES
                    ('01992a00-0000-7000-8000-000000000001', 'REPORTE_FURAT', 'CasoRadicado', 2, 'HABILES', 1, 'HABILES',
                     'Decreto 1295/1994, art. 62; Res. 156/2005', DATE '2000-01-01'),
                    ('01992a00-0000-7000-8000-000000000002', 'REPORTE_FUREP', 'CasoRadicado', 2, 'HABILES', 1, 'HABILES',
                     'Decreto 1295/1994, art. 62; Res. 156/2005', DATE '2000-01-01'),
                    ('01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', 'CasoRadicado', 15, 'CALENDARIO', 3, 'CALENDARIO',
                     'Res. 1401/2007; Decreto 1072/2015, art. 2.2.4.6.32', DATE '2000-01-01'),
                    ('01992a00-0000-7000-8000-000000000004', 'REMISION_INVESTIGACION_GRAVE', 'CasoRadicado', 15, 'CALENDARIO', 3, 'CALENDARIO',
                     'Res. 1401/2007', DATE '2000-01-01'),
                    ('01992a00-0000-7000-8000-000000000005', 'CONTROVERSIA_DICTAMEN', 'DictamenPclNotificado', 10, 'HABILES', 3, 'HABILES',
                     'Decreto-ley 019/2012, art. 142', DATE '2000-01-01'),
                    ('01992a00-0000-7000-8000-000000000006', 'TOPE_IT_180', 'IncapacidadRegistrada', 180, 'CALENDARIO', 30, 'CALENDARIO',
                     'Ley 776/2002, art. 3', DATE '2000-01-01');
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // halsa_app: only the tenant's plazos. halsa_worker: all of them, but only SELECT and UPDATE (its grants).
            migrationBuilder.Sql("""
                ALTER TABLE sst.plazos ENABLE ROW LEVEL SECURITY;
                ALTER TABLE sst.plazos FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_plazos_lectura ON sst.plazos FOR SELECT
                    USING (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_plazos_insercion ON sst.plazos FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_plazos_actualizacion ON sst.plazos FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_plazos_worker_lectura ON sst.plazos FOR SELECT TO halsa_worker USING (true);

                CREATE POLICY p_plazos_worker_actualizacion ON sst.plazos FOR UPDATE TO halsa_worker
                    USING (true) WITH CHECK (true);
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA sst FROM PUBLIC;
                GRANT USAGE ON SCHEMA sst TO halsa_app, halsa_worker;

                GRANT SELECT ON sst.reglas_termino, sst.festivos TO halsa_app;
                GRANT SELECT, INSERT, UPDATE ON sst.plazos TO halsa_app;

                GRANT SELECT ON sst.reglas_termino TO halsa_worker;
                GRANT SELECT, INSERT ON sst.festivos TO halsa_worker;
                GRANT SELECT, UPDATE ON sst.plazos TO halsa_worker;
                """);

            // ── halsa_worker: el mantenimiento ─────────────────────────────────
            // The idempotency purge sees only the expired keys. The outbox has no RLS. The audit partitions are created
            // by the function, now SECURITY DEFINER (owned by halsa_owner, with a fixed search_path).
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA integracion TO halsa_worker;
                GRANT SELECT, DELETE ON integracion.solicitudes_idempotentes TO halsa_worker;

                CREATE POLICY p_solicitudes_idempotentes_purga_lectura ON integracion.solicitudes_idempotentes
                    FOR SELECT TO halsa_worker USING (expira_en < now());
                CREATE POLICY p_solicitudes_idempotentes_purga ON integracion.solicitudes_idempotentes
                    FOR DELETE TO halsa_worker USING (expira_en < now());

                GRANT USAGE ON SCHEMA outbox TO halsa_worker;
                GRANT SELECT, INSERT, UPDATE ON outbox.outbox_messages TO halsa_worker;

                ALTER FUNCTION auditoria.asegurar_particiones(integer) SECURITY DEFINER;
                GRANT USAGE ON SCHEMA auditoria TO halsa_worker;
                GRANT EXECUTE ON FUNCTION auditoria.asegurar_particiones(integer) TO halsa_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The plazos' policies and grants fall with the tables; those on other schemas do not.
            migrationBuilder.Sql("""
                REVOKE EXECUTE ON FUNCTION auditoria.asegurar_particiones(integer) FROM halsa_worker;
                ALTER FUNCTION auditoria.asegurar_particiones(integer) SECURITY INVOKER;
                REVOKE USAGE ON SCHEMA auditoria FROM halsa_worker;

                REVOKE SELECT, INSERT, UPDATE ON outbox.outbox_messages FROM halsa_worker;
                REVOKE USAGE ON SCHEMA outbox FROM halsa_worker;

                DROP POLICY p_solicitudes_idempotentes_purga ON integracion.solicitudes_idempotentes;
                DROP POLICY p_solicitudes_idempotentes_purga_lectura ON integracion.solicitudes_idempotentes;
                REVOKE SELECT, DELETE ON integracion.solicitudes_idempotentes FROM halsa_worker;
                REVOKE USAGE ON SCHEMA integracion FROM halsa_worker;
                """);

            migrationBuilder.DropTable(
                name: "festivos",
                schema: "sst");

            migrationBuilder.DropTable(
                name: "plazos",
                schema: "sst");

            migrationBuilder.DropTable(
                name: "reglas_termino",
                schema: "sst");

            migrationBuilder.Sql("DROP SCHEMA sst;");
        }
    }
}
