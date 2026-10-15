using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations.Clinico
{
    /// <summary>
    /// SPEC 10: <c>halsa_clinico</c> encola en <c>outbox.outbox_messages</c> el evento de un registro clínico del backoffice,
    /// en la transacción del registro. Solo <c>INSERT</c>: no lee ni despacha el outbox. La tabla es de
    /// <c>ApplicationDbContext</c>, cuyas migraciones corren antes; este contexto la mapea sin migrarla.
    /// </summary>
    public partial class WebhooksSalientesClinico : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA outbox TO halsa_clinico;
                GRANT INSERT ON outbox.outbox_messages TO halsa_clinico;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE INSERT ON outbox.outbox_messages FROM halsa_clinico;
                REVOKE USAGE ON SCHEMA outbox FROM halsa_clinico;
                """);
        }
    }
}
