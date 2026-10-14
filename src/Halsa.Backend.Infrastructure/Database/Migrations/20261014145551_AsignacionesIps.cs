using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 08: la IPS asigna a su propio personal sobre los casos que tiene remitidos. <c>atel.asignaciones</c> gana
    /// <c>ips_id</c> (la IPS de quien asigna; <c>null</c> para la ARL) y su RLS de escritura admite a la IPS solo con
    /// su propio <c>ips_id</c> y una remisión vigente del caso.
    /// </summary>
    public partial class AsignacionesIps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ips_id",
                schema: "atel",
                table: "asignaciones",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            // The ARL writes only rows without ips_id in its tenant. The IPS writes only rows with its own ips_id,
            // over a case it has remitted and live, and with the remission's tenant pinned.
            migrationBuilder.Sql("""
                DROP POLICY p_asignaciones_insercion ON atel.asignaciones;
                DROP POLICY p_asignaciones_borrado ON atel.asignaciones;

                CREATE POLICY p_asignaciones_insercion ON atel.asignaciones FOR INSERT WITH CHECK (
                    (ips_id IS NULL AND tenant_id = current_setting('app.tenant_id', true))
                    OR (ips_id = current_setting('app.ips_id', true)
                        AND EXISTS (SELECT 1 FROM atel.remisiones r
                                    WHERE r.caso_medico_id = asignaciones.caso_medico_id
                                      AND r.tenant_id = asignaciones.tenant_id
                                      AND r.ips_id = asignaciones.ips_id
                                      AND r.revocada_en IS NULL)));

                CREATE POLICY p_asignaciones_borrado ON atel.asignaciones FOR DELETE USING (
                    tenant_id = current_setting('app.tenant_id', true)
                    OR (ips_id = current_setting('app.ips_id', true)
                        AND EXISTS (SELECT 1 FROM atel.remisiones r
                                    WHERE r.caso_medico_id = asignaciones.caso_medico_id
                                      AND r.tenant_id = asignaciones.tenant_id
                                      AND r.ips_id = asignaciones.ips_id
                                      AND r.revocada_en IS NULL)));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY p_asignaciones_insercion ON atel.asignaciones;
                DROP POLICY p_asignaciones_borrado ON atel.asignaciones;

                CREATE POLICY p_asignaciones_insercion ON atel.asignaciones FOR INSERT
                    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));

                CREATE POLICY p_asignaciones_borrado ON atel.asignaciones FOR DELETE
                    USING (tenant_id = current_setting('app.tenant_id', true));
                """);

            migrationBuilder.DropColumn(
                name: "ips_id",
                schema: "atel",
                table: "asignaciones");
        }
    }
}
