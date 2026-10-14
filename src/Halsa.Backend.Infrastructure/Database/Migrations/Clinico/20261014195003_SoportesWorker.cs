using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <summary>
    /// SPEC 09: <c>ArchivosHuerfanosJob</c> corre como <c>halsa_worker</c> y solo necesita saber qué archivos tienen
    /// metadata: <c>SELECT (id, ruta)</c> sobre <c>clinico.soportes</c>, de todos los tenants, y nada más del esquema.
    ///
    /// <c>p_soportes</c> pasa a aplicar solo a los roles que la usan: llama a <c>atel.tenant_del_caso_remitido</c>, que
    /// <c>halsa_worker</c> no puede ejecutar, y al combinarse con su política le haría fallar la lectura.
    /// </summary>
    public partial class SoportesWorker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA clinico TO halsa_worker;
                GRANT SELECT (id, ruta) ON clinico.soportes TO halsa_worker;

                ALTER POLICY p_soportes ON clinico.soportes TO halsa_owner, halsa_clinico, halsa_ingesta;

                CREATE POLICY p_soportes_worker ON clinico.soportes FOR SELECT TO halsa_worker USING (true);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY p_soportes_worker ON clinico.soportes;
                ALTER POLICY p_soportes ON clinico.soportes TO PUBLIC;
                REVOKE SELECT (id, ruta) ON clinico.soportes FROM halsa_worker;
                REVOKE USAGE ON SCHEMA clinico FROM halsa_worker;
                """);
        }
    }
}
