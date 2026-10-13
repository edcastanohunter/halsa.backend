using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 06: la integración de una IPS lee el caso antes de escribir su concepto médico. La política de lectura
    /// de <c>casos_medicos</c> consulta <c>atel.remisiones</c> con los permisos de quien lee, así que
    /// <c>halsa_ingesta</c> necesita <c>SELECT</c> sobre ella. La RLS de <c>remisiones</c> le deja ver solo las de
    /// su propio tenant o su propia IPS.
    /// </summary>
    public partial class ConceptosMedicosIngesta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT SELECT ON atel.remisiones TO halsa_ingesta;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE SELECT ON atel.remisiones FROM halsa_ingesta;");
        }
    }
}
