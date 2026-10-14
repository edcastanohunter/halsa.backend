using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 08: <c>auditoria.revisiones_emergencia</c>, la revisión obligatoria de cada acceso de emergencia
    /// (break-the-glass). Se inserta con la fila de <c>auditoria.accesos</c> que la origina. <c>halsa_app</c> solo
    /// actualiza las columnas del cierre, y un trigger impide cambiarlas una vez cerrada.
    /// </summary>
    public partial class RevisionesEmergencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "revisiones_emergencia",
                schema: "auditoria",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    acceso_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ocurrido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    vence_revision_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revisado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revisado_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    conclusion = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    observaciones = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisiones_emergencia", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_revisiones_emergencia_actor_caso",
                schema: "auditoria",
                table: "revisiones_emergencia",
                columns: new[] { "actor_id", "caso_medico_id", "ocurrido_en" });

            migrationBuilder.CreateIndex(
                name: "ix_revisiones_emergencia_tenant_pendientes",
                schema: "auditoria",
                table: "revisiones_emergencia",
                columns: new[] { "tenant_id", "revisado_en", "vence_revision_en" });

            // ── Restricciones ──────────────────────────────────────────────────
            // A review is open (nothing of the closure) or closed (all of it but the observations).
            migrationBuilder.Sql("""
                ALTER TABLE auditoria.revisiones_emergencia ADD CONSTRAINT ck_revisiones_emergencia_cierre CHECK (
                    (revisado_en IS NULL AND revisado_por IS NULL AND conclusion IS NULL AND observaciones IS NULL)
                    OR (revisado_en IS NOT NULL AND revisado_por IS NOT NULL AND conclusion IS NOT NULL
                        AND conclusion IN ('JUSTIFICADO', 'INJUSTIFICADO')));

                ALTER TABLE auditoria.revisiones_emergencia ADD CONSTRAINT ck_revisiones_emergencia_motivo
                    CHECK (char_length(motivo) BETWEEN 20 AND 500);
                """);

            // ── Inmutabilidad ──────────────────────────────────────────────────
            // Only the closure changes, once: a closed review stays as it was closed. No DELETE either, also for the
            // owner (the grants already keep halsa_app from both). As in auditoria.accesos, DELETE and TRUNCATE also get
            // statement triggers: with FORCE RLS and no DELETE policy, the owner's DELETE sees no rows, so a row trigger
            // would never fire and it would "work" silently, affecting 0 rows.
            migrationBuilder.Sql("""
                CREATE FUNCTION auditoria.proteger_revision_emergencia() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Una revisión de emergencia no se borra.';
                    END IF;
                    IF OLD.revisado_en IS NOT NULL THEN
                        RAISE EXCEPTION 'La revisión de emergencia % ya está cerrada.', OLD.id;
                    END IF;
                    IF (NEW.id, NEW.tenant_id, NEW.acceso_id, NEW.actor_id, NEW.caso_medico_id, NEW.motivo,
                        NEW.ocurrido_en, NEW.vence_revision_en)
                       IS DISTINCT FROM
                       (OLD.id, OLD.tenant_id, OLD.acceso_id, OLD.actor_id, OLD.caso_medico_id, OLD.motivo,
                        OLD.ocurrido_en, OLD.vence_revision_en) THEN
                        RAISE EXCEPTION 'Solo las columnas del cierre de una revisión de emergencia cambian.';
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER t_revisiones_emergencia_protegida BEFORE UPDATE OR DELETE ON auditoria.revisiones_emergencia
                    FOR EACH ROW EXECUTE FUNCTION auditoria.proteger_revision_emergencia();

                CREATE TRIGGER t_revisiones_emergencia_sin_borrado BEFORE DELETE OR TRUNCATE ON auditoria.revisiones_emergencia
                    FOR EACH STATEMENT EXECUTE FUNCTION auditoria.rechazar_modificacion();
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // Insertion open, as auditoria.accesos: an IPS opens reviews on cases of several tenants. Reading and closing,
            // by the case's tenant. An actor also sees its own pending reviews, so the 1 h grouping works for an IPS.
            migrationBuilder.Sql("""
                ALTER TABLE auditoria.revisiones_emergencia ENABLE ROW LEVEL SECURITY;
                ALTER TABLE auditoria.revisiones_emergencia FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_rev_insert ON auditoria.revisiones_emergencia FOR INSERT WITH CHECK (true);

                CREATE POLICY p_rev_select ON auditoria.revisiones_emergencia FOR SELECT
                    USING (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_rev_select_propias ON auditoria.revisiones_emergencia FOR SELECT
                    USING (actor_id = current_setting('app.sub', true) AND revisado_en IS NULL);

                CREATE POLICY p_rev_update ON auditoria.revisiones_emergencia FOR UPDATE
                    USING (tenant_id = current_setting('app.tenant_id', true))
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON auditoria.revisiones_emergencia TO halsa_app;
                GRANT UPDATE (revisado_en, revisado_por, conclusion, observaciones)
                    ON auditoria.revisiones_emergencia TO halsa_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies, the trigger and the grants fall with the table; the function does not.
            migrationBuilder.DropTable(
                name: "revisiones_emergencia",
                schema: "auditoria");

            migrationBuilder.Sql("DROP FUNCTION auditoria.proteger_revision_emergencia();");
        }
    }
}
