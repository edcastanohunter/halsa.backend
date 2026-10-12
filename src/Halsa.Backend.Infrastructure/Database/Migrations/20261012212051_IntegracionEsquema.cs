using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 05: el esquema <c>integracion</c>, <c>atel.reportes_evento</c>, las secuencias de radicado, las
    /// columnas de rechazo de <c>casos_medicos</c> y los permisos de <c>halsa_ingesta</c>. Los permisos sobre
    /// <c>clinico</c> van en <c>Clinico/IntegracionEsquemaClinico</c>: ese esquema lo crea la otra historia de
    /// migraciones, que corre después de esta.
    /// </summary>
    public partial class IntegracionEsquema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integracion");

            migrationBuilder.AddColumn<Guid>(
                name: "duplicado_de",
                schema: "atel",
                table: "casos_medicos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "motivo_rechazo",
                schema: "atel",
                table: "casos_medicos",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reportes_evento",
                schema: "atel",
                columns: table => new
                {
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    numero_reporte_origen = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    version = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    datos = table.Column<string>(type: "jsonb", nullable: false),
                    recibido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reportes_evento", x => x.caso_medico_id);
                    table.ForeignKey(
                        name: "fk_reportes_evento_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "solicitudes",
                schema: "integracion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    radicado = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    recibido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_solicitudes", x => x.id);
                    table.ForeignKey(
                        name: "fk_solicitudes_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "solicitudes_idempotentes",
                schema: "integracion",
                columns: table => new
                {
                    emisor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    cuerpo_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    solicitud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    respuesta = table.Column<string>(type: "jsonb", nullable: false),
                    creada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expira_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_solicitudes_idempotentes", x => new { x.emisor, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "mensajes_entrantes",
                schema: "integracion",
                columns: table => new
                {
                    solicitud_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    cuerpo_ct = table.Column<byte[]>(type: "bytea", nullable: false),
                    cuerpo_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    cuerpo_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    cuerpo_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    recibido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retener_hasta = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mensajes_entrantes", x => x.solicitud_id);
                    table.ForeignKey(
                        name: "fk_mensajes_entrantes_solicitudes",
                        column: x => x.solicitud_id,
                        principalSchema: "integracion",
                        principalTable: "solicitudes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_reportes_evento_tenant_tipo_numero",
                schema: "atel",
                table: "reportes_evento",
                columns: new[] { "tenant_id", "tipo", "numero_reporte_origen" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_solicitudes_caso_medico_id",
                schema: "integracion",
                table: "solicitudes",
                column: "caso_medico_id");

            migrationBuilder.CreateIndex(
                name: "ix_solicitudes_idempotentes_expira_en",
                schema: "integracion",
                table: "solicitudes_idempotentes",
                column: "expira_en");

            // ── Secuencias de radicado ─────────────────────────────────────────
            // Una por tenant y año, en una tabla con RLS: una SEQUENCE es global y revelaría el volumen de las
            // otras ARL. Solo se usa con SQL (INSERT ... ON CONFLICT DO UPDATE ... RETURNING), así que no se mapea.
            migrationBuilder.Sql("""
                CREATE TABLE atel.secuencias_radicado (
                    tenant_id character varying(20) NOT NULL,
                    anio      integer               NOT NULL,
                    ultimo    integer               NOT NULL,
                    CONSTRAINT pk_secuencias_radicado PRIMARY KEY (tenant_id, anio)
                );
                """);

            // ── Deduplicación ──────────────────────────────────────────────────
            // En SQL porque EF no admite un índice que mezcle columnas del dueño y de un owned type.
            migrationBuilder.Sql("""
                CREATE INDEX ix_casos_medicos_deduplicacion
                    ON atel.casos_medicos (tenant_id, trabajador_tipo, trabajador_numero, fecha_evento);
                """);

            // ── Inmutabilidad del mensaje crudo ────────────────────────────────
            // Es la evidencia de lo que reportó la ARL: nadie la modifica, tampoco el dueño. Por fila y por
            // sentencia, como en auditoria.accesos: con FORCE RLS la sentencia del dueño afectaría 0 filas en silencio.
            migrationBuilder.Sql("""
                CREATE FUNCTION integracion.rechazar_modificacion() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'El mensaje entrante es inmutable: % no está permitido en %.%',
                        TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME;
                END $$;

                CREATE TRIGGER t_mensajes_inmutable BEFORE UPDATE OR DELETE ON integracion.mensajes_entrantes
                    FOR EACH ROW EXECUTE FUNCTION integracion.rechazar_modificacion();
                CREATE TRIGGER t_mensajes_inmutable_sentencia BEFORE UPDATE OR DELETE ON integracion.mensajes_entrantes
                    FOR EACH STATEMENT EXECUTE FUNCTION integracion.rechazar_modificacion();
                CREATE TRIGGER t_mensajes_sin_truncate BEFORE TRUNCATE ON integracion.mensajes_entrantes
                    FOR EACH STATEMENT EXECUTE FUNCTION integracion.rechazar_modificacion();
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            // halsa_ingesta escribe la radicación completa en una transacción. SELECT sobre casos_medicos porque
            // el INSERT devuelve xmin; sobre las secuencias, por el ON CONFLICT ... RETURNING. Ni UPDATE ni DELETE
            // sobre casos, auditoría u outbox. halsa_app solo lee las solicitudes (consulta de estado): nada sobre
            // mensajes_entrantes ni solicitudes_idempotentes.
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA integracion FROM PUBLIC;

                GRANT USAGE ON SCHEMA atel, integracion, outbox, auditoria TO halsa_ingesta;

                GRANT SELECT, INSERT ON atel.casos_medicos, atel.reportes_evento TO halsa_ingesta;
                GRANT SELECT, INSERT, UPDATE ON atel.secuencias_radicado TO halsa_ingesta;
                GRANT SELECT, INSERT, UPDATE ON integracion.solicitudes, integracion.solicitudes_idempotentes,
                    integracion.mensajes_entrantes TO halsa_ingesta;
                GRANT INSERT ON outbox.outbox_messages TO halsa_ingesta;
                GRANT INSERT ON auditoria.accesos TO halsa_ingesta;

                -- La usan la política de clinico.historia_clinica_detalle y la RLS clínica de las IPS (SPEC 06).
                GRANT EXECUTE ON FUNCTION atel.tenant_del_caso_remitido(uuid, text) TO halsa_ingesta;

                GRANT USAGE ON SCHEMA integracion TO halsa_app;
                GRANT SELECT ON integracion.solicitudes TO halsa_app;
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // Por tenant, con FORCE para que también cubra al dueño. La idempotencia es por emisor: una ARL, una
            // IPS (SPEC 06) o un humano (SPEC 07) solo ven sus propias llaves.
            migrationBuilder.Sql("""
                ALTER TABLE atel.reportes_evento ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.reportes_evento FORCE ROW LEVEL SECURITY;
                ALTER TABLE atel.secuencias_radicado ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.secuencias_radicado FORCE ROW LEVEL SECURITY;
                ALTER TABLE integracion.solicitudes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE integracion.solicitudes FORCE ROW LEVEL SECURITY;
                ALTER TABLE integracion.solicitudes_idempotentes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE integracion.solicitudes_idempotentes FORCE ROW LEVEL SECURITY;
                ALTER TABLE integracion.mensajes_entrantes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE integracion.mensajes_entrantes FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_reportes_evento ON atel.reportes_evento FOR ALL
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_secuencias_radicado ON atel.secuencias_radicado FOR ALL
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_solicitudes ON integracion.solicitudes FOR ALL
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_mensajes_entrantes ON integracion.mensajes_entrantes FOR ALL
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_solicitudes_idempotentes ON integracion.solicitudes_idempotentes FOR ALL
                    USING (emisor = 'tenant:' || current_setting('app.tenant_id', true)
                        OR emisor = 'ips:' || current_setting('app.ips_id', true)
                        OR emisor = 'sub:' || current_setting('app.sub', true))
                    WITH CHECK (emisor = 'tenant:' || current_setting('app.tenant_id', true)
                        OR emisor = 'ips:' || current_setting('app.ips_id', true)
                        OR emisor = 'sub:' || current_setting('app.sub', true));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Políticas, triggers y la función caen con sus tablas y su esquema; los permisos fuera de integracion, no.
            migrationBuilder.Sql("""
                REVOKE EXECUTE ON FUNCTION atel.tenant_del_caso_remitido(uuid, text) FROM halsa_ingesta;
                REVOKE INSERT ON auditoria.accesos FROM halsa_ingesta;
                REVOKE INSERT ON outbox.outbox_messages FROM halsa_ingesta;
                REVOKE SELECT, INSERT ON atel.casos_medicos FROM halsa_ingesta;
                REVOKE USAGE ON SCHEMA atel, outbox, auditoria FROM halsa_ingesta;

                DROP INDEX atel.ix_casos_medicos_deduplicacion;
                DROP TABLE atel.secuencias_radicado;
                """);

            migrationBuilder.DropTable(
                name: "mensajes_entrantes",
                schema: "integracion");

            migrationBuilder.DropTable(
                name: "reportes_evento",
                schema: "atel");

            migrationBuilder.DropTable(
                name: "solicitudes_idempotentes",
                schema: "integracion");

            migrationBuilder.DropTable(
                name: "solicitudes",
                schema: "integracion");

            migrationBuilder.DropColumn(
                name: "duplicado_de",
                schema: "atel",
                table: "casos_medicos");

            migrationBuilder.DropColumn(
                name: "motivo_rechazo",
                schema: "atel",
                table: "casos_medicos");

            migrationBuilder.Sql("DROP SCHEMA integracion CASCADE;");
        }
    }
}
