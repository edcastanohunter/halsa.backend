using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Archivos;

/// <summary>
/// SPEC 07, step 3: <c>clinico.soportes</c> has the clinical history's RLS, is immutable even for its owner, and
/// only <c>halsa_ingesta</c> (insert) and <c>halsa_clinico</c> (select) reach it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SoportesBarrierTests(DatabaseFixture db)
{
    private const string Insert = """
        INSERT INTO clinico.soportes
          (id, tenant_id, caso_medico_id, tipo, ruta, key_id, sha256_claro, tamano, subido_en, retener_hasta)
        VALUES
          (@id, @tid, @caso, 'INCAPACIDAD', 'x/2026/10/x.bin', 'ARL_A:SOPORTES:v1', '\x00', 1, now(),
           current_date + interval '15 years')
        """;

    private static async Task InsertarAsync(NpgsqlConnection conn, Guid id, string tenantId, Guid caso)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = Insert;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("caso", caso);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SoporteAsync(string tenantId)
    {
        Guid caso = await InsertCasoAsync(db, tenantId);
        Guid id = Guid.CreateVersion7();

        await using NpgsqlConnection ingesta = await OpenAsync(db.IngestaConnectionString, tenantId: tenantId);
        await InsertarAsync(ingesta, id, tenantId, caso);

        return id;
    }

    [Fact]
    public async Task Ingesta_ShouldInsert_AndClinico_ShouldReadOnlyItsTenant()
    {
        Guid deA = await SoporteAsync(ArlA);
        Guid deB = await SoporteAsync(ArlB);

        await using NpgsqlConnection clinico = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlA);

        (await ScalarAsync(clinico, "SELECT count(*) FROM clinico.soportes WHERE id = @id", ("id", deA))).ShouldBe(1);
        (await ScalarAsync(clinico, "SELECT count(*) FROM clinico.soportes WHERE id = @id", ("id", deB))).ShouldBe(0);
    }

    [Fact]
    public async Task Ingesta_ShouldNotReadTheSupports()
    {
        Guid id = await SoporteAsync(ArlA);

        await using NpgsqlConnection ingesta = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(ingesta, "SELECT count(*) FROM clinico.soportes WHERE id = @id", ("id", id)));
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_ShouldNotReachTheSupports()
    {
        await using NpgsqlConnection app = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(app, "SELECT count(*) FROM clinico.soportes"));
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Ips_InsertingUnderAnotherTenantThanTheRemittedCase_ShouldViolateThePolicy()
    {
        Guid casoA = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, casoA, ArlA, Ips);

        await using NpgsqlConnection ingesta = await OpenAsync(db.IngestaConnectionString, ipsId: Ips);

        // The remitted case's tenant is accepted...
        await InsertarAsync(ingesta, Guid.CreateVersion7(), ArlA, casoA);

        // ...another one is not.
        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => InsertarAsync(ingesta, Guid.CreateVersion7(), ArlB, casoA));
        ex.MessageText.ShouldContain("new row violates row-level security policy");
    }

    [Theory]
    [InlineData("UPDATE clinico.soportes SET ruta = ruta WHERE id = @id")]
    [InlineData("DELETE FROM clinico.soportes WHERE id = @id")]
    public async Task Owner_UpdateOrDelete_ShouldBeRejectedByTheTrigger(string sql)
    {
        Guid id = await SoporteAsync(ArlA);

        await using NpgsqlConnection owner = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ScalarAsync(owner, sql, ("id", id)));
        ex.MessageText.ShouldContain("append-only");
    }

    [Fact]
    public async Task Owner_WithoutSessionVariables_ShouldStillBeRejectedByTheStatementTrigger()
    {
        await SoporteAsync(ArlA);

        // FORCE RLS hides every row from the owner here: only the statement-level trigger can refuse.
        await using NpgsqlConnection owner = await OpenAsync(db.OwnerConnectionString);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(owner, "DELETE FROM clinico.soportes"));
        ex.MessageText.ShouldContain("append-only");
    }
}
