using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class AtelFuncionRemisionClinica : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SPEC 03: la RLS clínica de la IPS necesita el tenant de un caso remitido, pero
            // halsa_clinico no tiene USAGE sobre atel. La función corre como su dueño (halsa_owner),
            // con search_path fijo, sin SQL dinámico, y devuelve solo el tenant_id.
            // El dueño sigue sujeto al FORCE RLS de atel, así que además solo ve el caso si
            // app.ips_id tiene una remisión vigente sobre él.
            migrationBuilder.Sql("""
                CREATE FUNCTION atel.tenant_del_caso_remitido(p_caso uuid, p_ips text) RETURNS text
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = atel, pg_temp AS $$
                    SELECT c.tenant_id FROM atel.casos_medicos c
                    JOIN atel.remisiones r ON r.caso_medico_id = c.id
                    WHERE c.id = p_caso AND r.ips_id = p_ips AND r.revocada_en IS NULL LIMIT 1 $$;

                -- PostgreSQL concede EXECUTE a PUBLIC por defecto: solo halsa_clinico la usa.
                REVOKE EXECUTE ON FUNCTION atel.tenant_del_caso_remitido(uuid, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION atel.tenant_del_caso_remitido(uuid, text) TO halsa_clinico;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION atel.tenant_del_caso_remitido(uuid, text);");
        }
    }
}
