using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <summary>
    /// SPEC 06: la integración de una IPS puede corregir un registro anterior (<c>corrigeA</c>), y para eso tiene que
    /// saber si ese registro existe en el mismo caso y es visible. <c>halsa_ingesta</c> sigue sin poder leer la HC
    /// (SPEC 05): esta función le responde solo sí o no.
    ///
    /// Corre como su dueño, que sigue sujeto al FORCE RLS de la HC: la política <c>p_hc</c> se evalúa con el
    /// <c>app.tenant_id</c> y el <c>app.ips_id</c> de la sesión, así que un registro invisible responde <c>false</c>,
    /// igual que uno que no existe. <c>search_path</c> fijo y sin SQL dinámico.
    /// </summary>
    public partial class ConceptosMedicosIngestaClinico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION clinico.registro_del_caso_visible(p_registro uuid, p_caso uuid) RETURNS boolean
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = clinico, pg_temp AS $$
                    SELECT EXISTS (SELECT 1 FROM clinico.historia_clinica_detalle h
                                   WHERE h.id = p_registro AND h.caso_medico_id = p_caso) $$;

                -- PostgreSQL concede EXECUTE a PUBLIC por defecto: solo la ingesta la usa.
                REVOKE EXECUTE ON FUNCTION clinico.registro_del_caso_visible(uuid, uuid) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION clinico.registro_del_caso_visible(uuid, uuid) TO halsa_ingesta;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION clinico.registro_del_caso_visible(uuid, uuid);");
        }
    }
}
