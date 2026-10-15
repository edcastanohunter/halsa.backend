using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 12: el portal del paciente. Los códigos de activación (RLS por tenant) y los vínculos por documento (RLS por
    /// <c>app.sub</c>), los intentos de canje, la función <c>SECURITY DEFINER</c> que canjea, y la cláusula de paciente en
    /// la lectura de <c>casos_medicos</c>, <c>remisiones</c>, <c>incapacidades</c> y <c>dictamenes_pcl</c>. Además,
    /// <c>validado_en</c> y <c>rechazado_en</c> del caso, para la línea de tiempo.
    /// </summary>
    public partial class PacienteVinculos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rechazado_en",
                schema: "atel",
                table: "casos_medicos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "validado_en",
                schema: "atel",
                table: "casos_medicos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "codigos_activacion",
                schema: "atel",
                columns: table => new
                {
                    id = table.Column<string>(type: "char(6)", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trabajador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    trabajador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    secreto_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    secreto_sal = table.Column<byte[]>(type: "bytea", nullable: false),
                    emitido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    emitido_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expira_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    usado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    usado_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    intentos_fallidos = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_codigos_activacion", x => x.id);
                    table.ForeignKey(
                        name: "fk_codigos_activacion_casos_medicos",
                        column: x => x.caso_medico_id,
                        principalSchema: "atel",
                        principalTable: "casos_medicos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vinculos_paciente",
                schema: "atel",
                columns: table => new
                {
                    paciente_sub = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trabajador_tipo = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    trabajador_numero = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    codigo_id = table.Column<string>(type: "char(6)", nullable: false),
                    vinculado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vinculos_paciente", x => new { x.paciente_sub, x.trabajador_tipo, x.trabajador_numero });
                    table.ForeignKey(
                        name: "fk_vinculos_paciente_codigos_activacion",
                        column: x => x.codigo_id,
                        principalSchema: "atel",
                        principalTable: "codigos_activacion",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_codigos_activacion_caso_activo",
                schema: "atel",
                table: "codigos_activacion",
                column: "caso_medico_id",
                unique: true,
                filter: "estado = 'ACTIVO'");

            migrationBuilder.CreateIndex(
                name: "ix_vinculos_paciente_codigo_id",
                schema: "atel",
                table: "vinculos_paciente",
                column: "codigo_id");

            migrationBuilder.CreateIndex(
                name: "ix_vinculos_paciente_trabajador",
                schema: "atel",
                table: "vinculos_paciente",
                columns: new[] { "trabajador_tipo", "trabajador_numero" });

            // ── Restricciones que EF no expresa ────────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE atel.codigos_activacion ADD CONSTRAINT ck_codigos_activacion_valores CHECK (
                    estado IN ('ACTIVO', 'USADO', 'BLOQUEADO', 'EXPIRADO')
                    AND id ~ '^[0-9A-HJKMNP-TV-Z]{6}$'
                    AND intentos_fallidos BETWEEN 0 AND 5
                    AND octet_length(secreto_hash) = 32
                    AND octet_length(secreto_sal) >= 16
                    AND expira_en > emitido_en
                    AND (estado <> 'USADO' OR (usado_en IS NOT NULL AND usado_por IS NOT NULL)));
                """);

            // ── Intentos de canje ──────────────────────────────────────────────
            // Only the redemption function reads and writes them: halsa_app has no grant. They limit the failures of a
            // patient to 5 in 24 h, whatever the code.
            migrationBuilder.Sql("""
                CREATE TABLE atel.intentos_canje (
                    paciente_sub character varying(64)     NOT NULL,
                    ocurrido_en  timestamp with time zone NOT NULL,
                    exitoso      boolean                  NOT NULL);

                CREATE INDEX ix_intentos_canje_paciente_ocurrido_en
                    ON atel.intentos_canje (paciente_sub, ocurrido_en DESC);
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // codigos_activacion: the ARL that issued it. The redemption function (owned by halsa_owner) sees only the
            // code it is redeeming, through app.canje_codigo, which it sets for its own transaction: the policy is TO
            // halsa_owner, so halsa_app gains nothing by setting the variable itself.
            // vinculos_paciente and intentos_canje: only the patient of the session.
            migrationBuilder.Sql("""
                ALTER TABLE atel.codigos_activacion ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.codigos_activacion FORCE ROW LEVEL SECURITY;
                ALTER TABLE atel.vinculos_paciente  ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.vinculos_paciente  FORCE ROW LEVEL SECURITY;
                ALTER TABLE atel.intentos_canje     ENABLE ROW LEVEL SECURITY;
                ALTER TABLE atel.intentos_canje     FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_codigos_activacion ON atel.codigos_activacion FOR ALL
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_codigos_activacion_canje ON atel.codigos_activacion FOR ALL TO halsa_owner
                    USING (id = current_setting('app.canje_codigo', true))
                    WITH CHECK (id = current_setting('app.canje_codigo', true));

                CREATE POLICY p_vinculos_paciente ON atel.vinculos_paciente FOR ALL
                    USING (current_setting('app.actor_tipo', true) = 'paciente'
                           AND paciente_sub = current_setting('app.sub', true))
                    WITH CHECK (current_setting('app.actor_tipo', true) = 'paciente'
                                AND paciente_sub = current_setting('app.sub', true));

                CREATE POLICY p_intentos_canje ON atel.intentos_canje FOR ALL
                    USING (current_setting('app.actor_tipo', true) = 'paciente'
                           AND paciente_sub = current_setting('app.sub', true))
                    WITH CHECK (current_setting('app.actor_tipo', true) = 'paciente'
                                AND paciente_sub = current_setting('app.sub', true));
                """);

            // ── La cláusula de paciente en la lectura del caso y de lo que cuelga de él ──
            // casos_medicos: the patient's links, by document (any tenant). The IPS branch now only runs with an
            // app.ips_id: remisiones calls atel.caso_visible_para_paciente, which reads casos_medicos, and without the
            // guard a patient's read would recurse through the remissions of the case.
            migrationBuilder.Sql("""
                DROP POLICY p_casos_lectura ON atel.casos_medicos;

                CREATE POLICY p_casos_lectura ON atel.casos_medicos FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR (coalesce(current_setting('app.ips_id', true), '') <> '' AND EXISTS (
                        SELECT 1 FROM atel.remisiones r
                        WHERE r.caso_medico_id = casos_medicos.id
                          AND r.ips_id = current_setting('app.ips_id', true)
                          AND r.revocada_en IS NULL))
                    OR (current_setting('app.actor_tipo', true) = 'paciente' AND EXISTS (
                        SELECT 1 FROM atel.vinculos_paciente v
                        WHERE v.paciente_sub = current_setting('app.sub', true)
                          AND v.trabajador_tipo = casos_medicos.trabajador_tipo
                          AND v.trabajador_numero = casos_medicos.trabajador_numero)));
                """);

            // remisiones: the policy of casos_medicos reads remisiones, so this one cannot read casos_medicos (the
            // planner rejects the cycle). The function runs as its owner, under the same casos_medicos RLS and the
            // session's variables: it answers only whether the current patient sees the case.
            migrationBuilder.Sql("""
                CREATE FUNCTION atel.caso_visible_para_paciente(p_caso uuid) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = atel, pg_temp AS $$
                    SELECT current_setting('app.actor_tipo', true) = 'paciente'
                       AND EXISTS (SELECT 1 FROM atel.casos_medicos c WHERE c.id = p_caso) $$;

                REVOKE EXECUTE ON FUNCTION atel.caso_visible_para_paciente(uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION atel.caso_visible_para_paciente(uuid) TO halsa_app, halsa_ingesta;

                DROP POLICY p_remisiones_lectura ON atel.remisiones;

                CREATE POLICY p_remisiones_lectura ON atel.remisiones FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR ips_id = current_setting('app.ips_id', true)
                    OR (current_setting('app.actor_tipo', true) = 'paciente'
                        AND atel.caso_visible_para_paciente(caso_medico_id)));
                """);

            // incapacidades and dictamenes_pcl: those of a case the patient sees. The RLS of casos_medicos is the link.
            migrationBuilder.Sql("""
                DROP POLICY p_incapacidades_lectura ON atel.incapacidades;

                CREATE POLICY p_incapacidades_lectura ON atel.incapacidades FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = incapacidades.caso_medico_id
                                 AND r.tenant_id = incapacidades.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL)
                    OR (current_setting('app.actor_tipo', true) = 'paciente' AND EXISTS (
                        SELECT 1 FROM atel.casos_medicos c WHERE c.id = incapacidades.caso_medico_id)));

                DROP POLICY p_dictamenes_pcl_lectura ON atel.dictamenes_pcl;

                CREATE POLICY p_dictamenes_pcl_lectura ON atel.dictamenes_pcl FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = dictamenes_pcl.caso_medico_id
                                 AND r.tenant_id = dictamenes_pcl.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL)
                    OR (current_setting('app.actor_tipo', true) = 'paciente' AND EXISTS (
                        SELECT 1 FROM atel.casos_medicos c WHERE c.id = dictamenes_pcl.caso_medico_id)));
                """);

            // ── El canje ───────────────────────────────────────────────────────
            // SECURITY DEFINER because the patient has no tenant and must not see anybody's codes. Fixed search_path, no
            // dynamic SQL. Only inside a patient session, and only for its own sub. In order:
            //   1. 5 failures of the patient in 24 h            → DEMASIADOS_INTENTOS (nothing else is looked at)
            //   2. absent, used or expired (marked EXPIRADO lazily) → INVALIDO, the same answer for the three
            //   3. BLOQUEADO                                     → BLOQUEADO
            //   4. wrong secret: intentos_fallidos + 1, and at 5 BLOQUEADO → INVALIDO
            //   5. the patient is already linked to another document → VINCULO_DISTINTO (the code stays ACTIVO)
            //   6. otherwise: USADO and the link (idempotent for the same document) → VINCULADO
            // 2, 3 and 4 count as failures of the patient. tenant_id and caso_medico_id come back whenever the code
            // exists, for the audit row; the document, only with 5 and 6.
            migrationBuilder.Sql("""
                CREATE FUNCTION atel.canjear_codigo_activacion(p_id text, p_secreto text, p_sub text)
                    RETURNS TABLE (resultado text, tenant_id text, caso_medico_id uuid,
                                   trabajador_tipo text, trabajador_numero text)
                    LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = atel, pg_temp AS $$
                DECLARE
                    v_codigo   atel.codigos_activacion%ROWTYPE;
                    v_vinculo  atel.vinculos_paciente%ROWTYPE;
                    v_fallidos integer;
                BEGIN
                    IF current_setting('app.actor_tipo', true) IS DISTINCT FROM 'paciente'
                       OR coalesce(p_sub, '') = ''
                       OR p_sub IS DISTINCT FROM current_setting('app.sub', true) THEN
                        RAISE EXCEPTION 'El canje solo corre en la sesión del propio paciente.'
                            USING ERRCODE = 'insufficient_privilege';
                    END IF;

                    SELECT count(*) INTO v_fallidos FROM atel.intentos_canje i
                    WHERE i.paciente_sub = p_sub AND NOT i.exitoso AND i.ocurrido_en > now() - interval '24 hours';

                    IF v_fallidos >= 5 THEN
                        RETURN QUERY SELECT 'DEMASIADOS_INTENTOS'::text, NULL::text, NULL::uuid, NULL::text, NULL::text;
                        RETURN;
                    END IF;

                    PERFORM set_config('app.canje_codigo', coalesce(p_id, ''), true);

                    SELECT * INTO v_codigo FROM atel.codigos_activacion c WHERE c.id = p_id FOR UPDATE;

                    IF NOT FOUND THEN
                        INSERT INTO atel.intentos_canje VALUES (p_sub, now(), false);
                        PERFORM set_config('app.canje_codigo', '', true);
                        RETURN QUERY SELECT 'INVALIDO'::text, NULL::text, NULL::uuid, NULL::text, NULL::text;
                        RETURN;
                    END IF;

                    IF v_codigo.estado = 'ACTIVO' AND v_codigo.expira_en <= now() THEN
                        UPDATE atel.codigos_activacion SET estado = 'EXPIRADO' WHERE id = p_id;
                        v_codigo.estado := 'EXPIRADO';
                    END IF;

                    IF v_codigo.estado IN ('USADO', 'EXPIRADO', 'BLOQUEADO') THEN
                        INSERT INTO atel.intentos_canje VALUES (p_sub, now(), false);
                        PERFORM set_config('app.canje_codigo', '', true);
                        RETURN QUERY SELECT
                            CASE WHEN v_codigo.estado = 'BLOQUEADO' THEN 'BLOQUEADO' ELSE 'INVALIDO' END,
                            v_codigo.tenant_id::text, v_codigo.caso_medico_id, NULL::text, NULL::text;
                        RETURN;
                    END IF;

                    IF sha256(v_codigo.secreto_sal || convert_to(coalesce(p_secreto, ''), 'UTF8'))
                           IS DISTINCT FROM v_codigo.secreto_hash THEN
                        UPDATE atel.codigos_activacion
                        SET intentos_fallidos = v_codigo.intentos_fallidos + 1,
                            estado = CASE WHEN v_codigo.intentos_fallidos + 1 >= 5 THEN 'BLOQUEADO' ELSE estado END
                        WHERE id = p_id;
                        INSERT INTO atel.intentos_canje VALUES (p_sub, now(), false);
                        PERFORM set_config('app.canje_codigo', '', true);
                        RETURN QUERY SELECT 'INVALIDO'::text, v_codigo.tenant_id::text, v_codigo.caso_medico_id,
                                            NULL::text, NULL::text;
                        RETURN;
                    END IF;

                    SELECT * INTO v_vinculo FROM atel.vinculos_paciente v WHERE v.paciente_sub = p_sub LIMIT 1;

                    IF FOUND AND (v_vinculo.trabajador_tipo, v_vinculo.trabajador_numero)
                                 IS DISTINCT FROM (v_codigo.trabajador_tipo, v_codigo.trabajador_numero) THEN
                        PERFORM set_config('app.canje_codigo', '', true);
                        RETURN QUERY SELECT 'VINCULO_DISTINTO'::text, v_codigo.tenant_id::text, v_codigo.caso_medico_id,
                                            v_codigo.trabajador_tipo::text, v_codigo.trabajador_numero::text;
                        RETURN;
                    END IF;

                    UPDATE atel.codigos_activacion SET estado = 'USADO', usado_en = now(), usado_por = p_sub
                    WHERE id = p_id;

                    INSERT INTO atel.vinculos_paciente
                        (paciente_sub, trabajador_tipo, trabajador_numero, codigo_id, vinculado_en)
                    VALUES (p_sub, v_codigo.trabajador_tipo, v_codigo.trabajador_numero, p_id, now())
                    ON CONFLICT DO NOTHING;

                    INSERT INTO atel.intentos_canje VALUES (p_sub, now(), true);
                    PERFORM set_config('app.canje_codigo', '', true);

                    RETURN QUERY SELECT 'VINCULADO'::text, v_codigo.tenant_id::text, v_codigo.caso_medico_id,
                                        v_codigo.trabajador_tipo::text, v_codigo.trabajador_numero::text;
                END $$;

                REVOKE EXECUTE ON FUNCTION atel.canjear_codigo_activacion(text, text, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION atel.canjear_codigo_activacion(text, text, text) TO halsa_app;
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            // The policy of casos_medicos reads vinculos_paciente with the privileges of whoever reads the case, so
            // halsa_ingesta needs SELECT too; its sessions are never 'paciente', so it sees no row.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE ON atel.codigos_activacion TO halsa_app;
                GRANT SELECT ON atel.vinculos_paciente TO halsa_app, halsa_ingesta;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The read policies as AtelCasos, AtelIncapacidades and AtelDictamenesPcl left them.
            migrationBuilder.Sql("""
                DROP FUNCTION atel.canjear_codigo_activacion(text, text, text);

                DROP POLICY p_casos_lectura ON atel.casos_medicos;
                CREATE POLICY p_casos_lectura ON atel.casos_medicos FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = casos_medicos.id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                DROP POLICY p_remisiones_lectura ON atel.remisiones;
                CREATE POLICY p_remisiones_lectura ON atel.remisiones FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR ips_id = current_setting('app.ips_id', true));

                DROP POLICY p_incapacidades_lectura ON atel.incapacidades;
                CREATE POLICY p_incapacidades_lectura ON atel.incapacidades FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = incapacidades.caso_medico_id
                                 AND r.tenant_id = incapacidades.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                DROP POLICY p_dictamenes_pcl_lectura ON atel.dictamenes_pcl;
                CREATE POLICY p_dictamenes_pcl_lectura ON atel.dictamenes_pcl FOR SELECT USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR EXISTS (SELECT 1 FROM atel.remisiones r
                               WHERE r.caso_medico_id = dictamenes_pcl.caso_medico_id
                                 AND r.tenant_id = dictamenes_pcl.tenant_id
                                 AND r.ips_id = current_setting('app.ips_id', true)
                                 AND r.revocada_en IS NULL));

                DROP FUNCTION atel.caso_visible_para_paciente(uuid);
                DROP TABLE atel.intentos_canje;
                """);

            migrationBuilder.DropTable(
                name: "vinculos_paciente",
                schema: "atel");

            migrationBuilder.DropTable(
                name: "codigos_activacion",
                schema: "atel");

            migrationBuilder.DropColumn(
                name: "rechazado_en",
                schema: "atel",
                table: "casos_medicos");

            migrationBuilder.DropColumn(
                name: "validado_en",
                schema: "atel",
                table: "casos_medicos");
        }
    }
}
