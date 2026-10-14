using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <summary>
    /// SPEC 07: <c>clinico.soportes</c>, la metadata de los PDF de incapacidad (el archivo vive cifrado en el volumen).
    /// La misma RLS que la historia clínica, inmutable incluso para el dueño, y solo dos permisos: <c>halsa_ingesta</c>
    /// inserta (la carga escribe todo en una transacción) y <c>halsa_clinico</c> lee (la descarga, SPEC 08).
    /// </summary>
    public partial class IncapacidadesSoportes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "soportes",
                schema: "clinico",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caso_medico_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ruta = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sha256_claro = table.Column<byte[]>(type: "bytea", nullable: false),
                    tamano = table.Column<long>(type: "bigint", nullable: false),
                    subido_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retener_hasta = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_soportes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_soportes_tenant_caso",
                schema: "clinico",
                table: "soportes",
                columns: new[] { "tenant_id", "caso_medico_id" });

            // ── RLS ────────────────────────────────────────────────────────────
            // The clinical history's policy (p_hc): the tenant, or the tenant of a case remitted to the IPS. WITH
            // CHECK keeps an IPS from writing a support under another tenant than the case's.
            migrationBuilder.Sql("""
                ALTER TABLE clinico.soportes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE clinico.soportes FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_soportes ON clinico.soportes FOR ALL
                USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR tenant_id = atel.tenant_del_caso_remitido(caso_medico_id, current_setting('app.ips_id', true)))
                WITH CHECK (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR tenant_id = atel.tenant_del_caso_remitido(caso_medico_id, current_setting('app.ips_id', true)));
                """);

            // ── Inmutabilidad ──────────────────────────────────────────────────
            // Per row and per statement, as in integracion.mensajes_entrantes: under FORCE RLS a statement of the
            // owner that sees no row would otherwise succeed silently.
            migrationBuilder.Sql("""
                CREATE TRIGGER t_soportes_inmutable BEFORE UPDATE OR DELETE ON clinico.soportes
                    FOR EACH ROW EXECUTE FUNCTION clinico.rechazar_modificacion();
                CREATE TRIGGER t_soportes_inmutable_sentencia BEFORE UPDATE OR DELETE ON clinico.soportes
                    FOR EACH STATEMENT EXECUTE FUNCTION clinico.rechazar_modificacion();
                CREATE TRIGGER t_soportes_sin_truncate BEFORE TRUNCATE ON clinico.soportes
                    FOR EACH STATEMENT EXECUTE FUNCTION clinico.rechazar_modificacion();
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                GRANT INSERT ON clinico.soportes TO halsa_ingesta;
                GRANT SELECT ON clinico.soportes TO halsa_clinico;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policy, the triggers and the grants fall with the table.
            migrationBuilder.DropTable(
                name: "soportes",
                schema: "clinico");
        }
    }
}
