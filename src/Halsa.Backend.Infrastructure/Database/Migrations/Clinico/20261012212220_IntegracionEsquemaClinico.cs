using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <summary>
    /// SPEC 05: la parte clínica de los permisos de <c>halsa_ingesta</c>. Va en esta historia porque el esquema
    /// <c>clinico</c> no existe cuando corre <c>IntegracionEsquema</c> de <see cref="ApplicationDbContext"/>.
    ///
    /// Sobre la historia clínica, solo <c>INSERT</c>: la ingesta escribe el <c>REPORTE_INICIAL</c> en la misma
    /// transacción que el caso, pero no puede leer la HC. La política <c>p_hc</c> ya exige que el tenant del
    /// registro sea el de <c>app.tenant_id</c>.
    /// </summary>
    public partial class IntegracionEsquemaClinico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA clinico TO halsa_ingesta;
                GRANT INSERT ON clinico.historia_clinica_detalle TO halsa_ingesta;
                GRANT SELECT, INSERT ON clinico.llaves_tenant TO halsa_ingesta;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE SELECT, INSERT ON clinico.llaves_tenant FROM halsa_ingesta;
                REVOKE INSERT ON clinico.historia_clinica_detalle FROM halsa_ingesta;
                REVOKE USAGE ON SCHEMA clinico FROM halsa_ingesta;
                """);
        }
    }
}
