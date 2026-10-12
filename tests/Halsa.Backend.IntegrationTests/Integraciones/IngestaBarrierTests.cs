using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Integraciones;

/// <summary>
/// SPEC 05, paso 1: los permisos del rol <c>halsa_ingesta</c> y del esquema <c>integracion</c>. Conexiones
/// crudas, para probar que la base los impone por sí sola.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class IngestaBarrierTests(DatabaseFixture db)
{
    [Fact]
    public async Task Ingesta_ShouldNotReadTheClinicalHistory()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "SELECT 1 FROM clinico.historia_clinica_detalle"));

        ex.MessageText.ShouldBe("permission denied for table historia_clinica_detalle");
    }

    [Theory]
    [InlineData("UPDATE atel.casos_medicos SET estado = estado")]
    [InlineData("DELETE FROM atel.casos_medicos")]
    [InlineData("DELETE FROM atel.reportes_evento")]
    [InlineData("UPDATE outbox.outbox_messages SET attempts = 0")]
    [InlineData("SELECT 1 FROM auditoria.accesos")]
    [InlineData("SELECT 1 FROM atel.remisiones")]
    public async Task Ingesta_ShouldNotModifyOrReadBeyondItsGrants(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ScalarAsync(conn, sql));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("mensajes_entrantes")]
    [InlineData("solicitudes_idempotentes")]
    public async Task App_ShouldNotReachTheRawMessagesNorTheIdempotencyKeys(string tabla)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, $"SELECT 1 FROM integracion.{tabla}"));

        ex.MessageText.ShouldBe($"permission denied for table {tabla}");
    }

    [Fact]
    public async Task App_ShouldReadTheRequests_OfItsTenantOnly()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        (await ScalarAsync(conn, "SELECT count(*) FROM integracion.solicitudes WHERE tenant_id <> 'ARL_A'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Secuencias_ShouldBeIsolatedByTenant()
    {
        const string siguiente = """
            INSERT INTO atel.secuencias_radicado (tenant_id, anio, ultimo) VALUES (@tenant, 1999, 1)
            ON CONFLICT (tenant_id, anio) DO UPDATE SET ultimo = secuencias_radicado.ultimo + 1
            RETURNING ultimo
            """;

        await using NpgsqlConnection a = await OpenAsync(db.IngestaConnectionString, tenantId: "ARL_SEQ_A");
        long primeroA = await ScalarAsync(a, siguiente, ("tenant", "ARL_SEQ_A"));
        long segundoA = await ScalarAsync(a, siguiente, ("tenant", "ARL_SEQ_A"));

        await using NpgsqlConnection b = await OpenAsync(db.IngestaConnectionString, tenantId: "ARL_SEQ_B");
        long primeroB = await ScalarAsync(b, siguiente, ("tenant", "ARL_SEQ_B"));

        (primeroA, segundoA, primeroB).ShouldBe((1L, 2L, 1L));
        (await ScalarAsync(b, "SELECT count(*) FROM atel.secuencias_radicado WHERE tenant_id = 'ARL_SEQ_A'"))
            .ShouldBe(0);

        // Another tenant's row cannot be written either.
        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(b, siguiente, ("tenant", "ARL_SEQ_A")));
        ex.MessageText.ShouldContain("row-level security");
    }
}
