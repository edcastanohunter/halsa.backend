using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 04: <c>auditoria.accesos</c>, particionada por mes e inmutable. EF no sabe crear una tabla
    /// particionada, así que la tabla va en SQL; el snapshot la describe tal como la mapea el modelo.
    /// </summary>
    public partial class AuditoriaAccesos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "auditoria");

            // ── Tabla particionada ─────────────────────────────────────────────
            // La PK incluye ocurrido_en porque PostgreSQL exige la clave de partición en toda restricción única.
            // La partición DEFAULT es la red de seguridad: un insert nunca falla porque falte el mes.
            migrationBuilder.Sql("""
                CREATE TABLE auditoria.accesos (
                    id              uuid                     NOT NULL,
                    ocurrido_en     timestamp with time zone NOT NULL,
                    tenant_id       character varying(20)    NULL,
                    actor_id        character varying(64)    NOT NULL,
                    actor_tipo      character varying(20)    NOT NULL,
                    actor_tenant_id character varying(20)    NULL,
                    actor_ips_id    character varying(12)    NULL,
                    actor_roles     text[]                   NOT NULL,
                    client_id       character varying(100)   NULL,
                    ip              inet                     NULL,
                    user_agent      character varying(300)   NULL,
                    correlation_id  character varying(64)    NOT NULL,
                    accion          character varying(20)    NOT NULL,
                    resultado       character varying(20)    NOT NULL,
                    recurso_tipo    character varying(30)    NOT NULL,
                    recurso_id      character varying(64)    NULL,
                    caso_medico_id  uuid                     NULL,
                    motivo          character varying(500)   NULL,
                    acr             character varying(10)    NULL,
                    hash_registro   bytea                    NOT NULL,
                    CONSTRAINT pk_accesos PRIMARY KEY (id, ocurrido_en)
                ) PARTITION BY RANGE (ocurrido_en);

                CREATE TABLE auditoria.accesos_default PARTITION OF auditoria.accesos DEFAULT;

                CREATE INDEX ix_accesos_tenant_caso_ocurrido_en
                    ON auditoria.accesos (tenant_id, caso_medico_id, ocurrido_en);
                CREATE INDEX ix_accesos_tenant_actor_ocurrido_en
                    ON auditoria.accesos (tenant_id, actor_id, ocurrido_en);
                """);

            // ── Inmutabilidad ──────────────────────────────────────────────────
            // También para el dueño. Por fila, porque los triggers de fila se clonan a cada partición.
            // Por sentencia, porque con FORCE RLS y sin política de UPDATE/DELETE la sentencia del dueño
            // no ve filas: sin este trigger "funcionaría" en silencio, afectando 0 filas.
            // TRUNCATE es por sentencia y no se clona: asegurar_particiones lo pone en cada partición nueva.
            migrationBuilder.Sql("""
                CREATE FUNCTION auditoria.rechazar_modificacion() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'La auditoría de accesos es inmutable: % no está permitido en %.%',
                        TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME;
                END $$;

                CREATE TRIGGER t_accesos_inmutable BEFORE UPDATE OR DELETE ON auditoria.accesos
                    FOR EACH ROW EXECUTE FUNCTION auditoria.rechazar_modificacion();
                CREATE TRIGGER t_accesos_inmutable_sentencia BEFORE UPDATE OR DELETE ON auditoria.accesos
                    FOR EACH STATEMENT EXECUTE FUNCTION auditoria.rechazar_modificacion();
                CREATE TRIGGER t_accesos_sin_truncate BEFORE TRUNCATE ON auditoria.accesos
                    FOR EACH STATEMENT EXECUTE FUNCTION auditoria.rechazar_modificacion();
                CREATE TRIGGER t_accesos_sin_truncate BEFORE TRUNCATE ON auditoria.accesos_default
                    FOR EACH STATEMENT EXECUTE FUNCTION auditoria.rechazar_modificacion();
                """);

            // ── Particiones mensuales ──────────────────────────────────────────
            // Crea el mes actual (UTC) y los meses_adelante siguientes. Idempotente. Sin pg_partman, que
            // obligaría a una imagen de PostgreSQL propia en el VPS. Se llama después de migrar, y la
            // llamará el job diario de la SPEC 09.
            // Si accesos_default ya tiene filas de un mes, crear su partición fallaría: ese mes se salta
            // con un WARNING y sus filas siguen en DEFAULT, que es lo que la red de seguridad promete.
            migrationBuilder.Sql("""
                CREATE FUNCTION auditoria.asegurar_particiones(meses_adelante integer) RETURNS void
                    LANGUAGE plpgsql SET search_path = auditoria, pg_temp AS $$
                DECLARE
                    mes_actual timestamp := date_trunc('month', now() AT TIME ZONE 'UTC');
                    mes        timestamp;
                    nombre     text;
                BEGIN
                    FOR i IN 0..meses_adelante LOOP
                        mes    := mes_actual + make_interval(months => i);
                        nombre := 'accesos_' || to_char(mes, 'YYYY_MM');

                        CONTINUE WHEN to_regclass(format('auditoria.%I', nombre)) IS NOT NULL;

                        IF EXISTS (SELECT 1 FROM auditoria.accesos_default
                                   WHERE ocurrido_en >= mes AT TIME ZONE 'UTC'
                                     AND ocurrido_en <  (mes + interval '1 month') AT TIME ZONE 'UTC') THEN
                            RAISE WARNING 'auditoria.accesos_default ya tiene filas de %; no se crea %', mes, nombre;
                            CONTINUE;
                        END IF;

                        EXECUTE format(
                            'CREATE TABLE auditoria.%I PARTITION OF auditoria.accesos FOR VALUES FROM (%L) TO (%L)',
                            nombre, mes AT TIME ZONE 'UTC', (mes + interval '1 month') AT TIME ZONE 'UTC');
                        EXECUTE format(
                            'CREATE TRIGGER t_accesos_sin_truncate BEFORE TRUNCATE ON auditoria.%I '
                            'FOR EACH STATEMENT EXECUTE FUNCTION auditoria.rechazar_modificacion()',
                            nombre);
                    END LOOP;
                END $$;

                REVOKE EXECUTE ON FUNCTION auditoria.asegurar_particiones(integer) FROM PUBLIC;
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            // Los privilegios se comprueban sobre la tabla padre. Las particiones no reciben ninguno, así
            // que solo el dueño puede leerlas directamente (y saltarse así la RLS del padre).
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA auditoria FROM PUBLIC;

                GRANT USAGE ON SCHEMA auditoria TO halsa_app, halsa_clinico;
                GRANT INSERT, SELECT ON auditoria.accesos TO halsa_app;
                GRANT INSERT ON auditoria.accesos TO halsa_clinico;
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // Inserción abierta: una IPS registra accesos a casos de varios tenants. La protección contra la
            // manipulación son los triggers (y el sellado de la SPEC 11). La lectura, por tenant del recurso.
            migrationBuilder.Sql("""
                ALTER TABLE auditoria.accesos ENABLE ROW LEVEL SECURITY;
                ALTER TABLE auditoria.accesos FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_aud_insert ON auditoria.accesos FOR INSERT WITH CHECK (true);
                CREATE POLICY p_aud_select ON auditoria.accesos FOR SELECT
                    USING (tenant_id = current_setting('app.tenant_id', true));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Particiones, triggers y políticas caen con la tabla; la tabla y las funciones, con el esquema.
            migrationBuilder.Sql("""
                REVOKE USAGE ON SCHEMA auditoria FROM halsa_app, halsa_clinico;
                DROP SCHEMA auditoria CASCADE;
                """);
        }
    }
}
