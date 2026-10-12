using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <inheritdoc />
    public partial class ClinicoInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "clinico");

            migrationBuilder.CreateTable(
                name: "historia_clinica_detalle",
                schema: "clinico",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    profesional_sub = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    profesional_tipo_documento = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    profesional_numero_documento = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    profesional_registro_rethus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    atendido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registrado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retener_hasta = table.Column<DateOnly>(type: "date", nullable: false),
                    corrige_a = table.Column<Guid>(type: "uuid", nullable: true),
                    contenido_ct = table.Column<byte[]>(type: "bytea", nullable: false),
                    contenido_nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    contenido_tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    contenido_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    diagnostico_ct = table.Column<byte[]>(type: "bytea", nullable: true),
                    diagnostico_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    diagnostico_tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    diagnostico_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    diagnostico_indice_ciego = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_historia_clinica_detalle", x => x.id);
                    table.ForeignKey(
                        name: "fk_historia_clinica_detalle_corrige_a",
                        column: x => x.corrige_a,
                        principalSchema: "clinico",
                        principalTable: "historia_clinica_detalle",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "llaves_tenant",
                schema: "clinico",
                columns: table => new
                {
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    proposito = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    kek_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dek_envuelta = table.Column<byte[]>(type: "bytea", nullable: false),
                    creada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activa = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llaves_tenant", x => x.key_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_historia_clinica_detalle_corrige_a",
                schema: "clinico",
                table: "historia_clinica_detalle",
                column: "corrige_a");

            migrationBuilder.CreateIndex(
                name: "ix_historia_clinica_detalle_tenant_caso_registrado_en",
                schema: "clinico",
                table: "historia_clinica_detalle",
                columns: new[] { "tenant_id", "caso_medico_id", "registrado_en" });

            migrationBuilder.CreateIndex(
                name: "ix_historia_clinica_detalle_tenant_indice_ciego",
                schema: "clinico",
                table: "historia_clinica_detalle",
                columns: new[] { "tenant_id", "diagnostico_indice_ciego" });

            migrationBuilder.CreateIndex(
                name: "ux_llaves_tenant_activa",
                schema: "clinico",
                table: "llaves_tenant",
                columns: new[] { "tenant_id", "proposito" },
                unique: true,
                filter: "activa");

            // ── Permisos ───────────────────────────────────────────────────────
            // Las migraciones corren como halsa_owner. halsa_app no recibe nada sobre clinico: sin
            // USAGE, ni siquiera un FromSqlRaw llega. halsa_clinico solo lee y da de alta.
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA clinico FROM PUBLIC;
                REVOKE ALL ON SCHEMA clinico FROM halsa_app;

                GRANT USAGE ON SCHEMA clinico TO halsa_clinico;
                GRANT SELECT, INSERT ON clinico.historia_clinica_detalle TO halsa_clinico;
                GRANT SELECT, INSERT ON clinico.llaves_tenant TO halsa_clinico;
                """);

            // ── Función para el query filter de ClinicalDbContext ──────────────
            // El SQL que genera EF nombra la función, y halsa_clinico no puede nombrar nada en atel.
            // Este envoltorio usa un cuerpo SQL estándar (RETURN), que PostgreSQL resuelve al crearlo:
            // al llamarlo solo se comprueba EXECUTE, no USAGE sobre atel. La política p_hc llama a
            // atel.tenant_del_caso_remitido directamente, porque también se resuelve al crearla.
            migrationBuilder.Sql("""
                CREATE FUNCTION clinico.tenant_del_caso_remitido(p_caso uuid, p_ips text) RETURNS text
                    LANGUAGE sql STABLE
                    RETURN atel.tenant_del_caso_remitido(p_caso, p_ips);

                REVOKE EXECUTE ON FUNCTION clinico.tenant_del_caso_remitido(uuid, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION clinico.tenant_del_caso_remitido(uuid, text) TO halsa_clinico;
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // FORCE aplica la política también al dueño. llaves_tenant no lleva RLS: una IPS escribe
            // en casos de varios tenants y necesita la DEK de cada uno, y sin la KEK no sirven de nada.
            // WITH CHECK impide que una IPS inserte con un tenant_id distinto al del caso remitido.
            migrationBuilder.Sql("""
                ALTER TABLE clinico.historia_clinica_detalle ENABLE ROW LEVEL SECURITY;
                ALTER TABLE clinico.historia_clinica_detalle FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_hc ON clinico.historia_clinica_detalle FOR ALL
                USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR tenant_id = atel.tenant_del_caso_remitido(caso_medico_id, current_setting('app.ips_id', true)))
                WITH CHECK (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR tenant_id = atel.tenant_del_caso_remitido(caso_medico_id, current_setting('app.ips_id', true)));
                """);

            // ── Inmutabilidad ──────────────────────────────────────────────────
            // Append-only incluso para el dueño (Res. 1995/1999): una corrección es un registro nuevo.
            migrationBuilder.Sql("""
                CREATE FUNCTION clinico.rechazar_modificacion() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'La historia clínica es append-only: % no está permitido en %.%',
                        TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME;
                END $$;

                CREATE TRIGGER t_hc_inmutable BEFORE UPDATE OR DELETE ON clinico.historia_clinica_detalle
                    FOR EACH ROW EXECUTE FUNCTION clinico.rechazar_modificacion();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policy and the trigger fall with their table; the functions and grants do not.
            migrationBuilder.Sql("DROP FUNCTION clinico.tenant_del_caso_remitido(uuid, text);");

            migrationBuilder.DropTable(
                name: "historia_clinica_detalle",
                schema: "clinico");

            migrationBuilder.DropTable(
                name: "llaves_tenant",
                schema: "clinico");

            migrationBuilder.Sql("""
                DROP FUNCTION clinico.rechazar_modificacion();
                REVOKE USAGE ON SCHEMA clinico FROM halsa_clinico;
                """);
        }
    }
}
