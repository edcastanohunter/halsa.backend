using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Auditoria;

/// <summary>
/// SPEC 08, step 7: <c>auditoria.revisiones_emergencia</c> protects itself. <c>halsa_app</c> only touches the closure
/// columns, a closed review never changes, and each tenant only sees its own. Raw connections.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RevisionesEmergenciaTablaTests(DatabaseFixture db)
{
    private const string Actor = "sub-emergencia";

    private static async Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A pending review of ARL_A, inserted as halsa_app (the insertion is open, as for the audit rows).</summary>
    private async Task<Guid> InsertRevisionAsync(string actor = Actor)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, ipsId: "110011234501");

        Guid id = Guid.CreateVersion7();
        await ExecuteAsync(conn, """
            INSERT INTO auditoria.revisiones_emergencia
              (id, tenant_id, acceso_id, actor_id, caso_medico_id, motivo, ocurrido_en, vence_revision_en)
            VALUES (@id, 'ARL_A', gen_random_uuid(), @actor, gen_random_uuid(), 'Paciente en urgencias vit',
                    now(), now() + interval '72 hours')
            """, ("id", id), ("actor", actor));

        return id;
    }

    private const string Cerrar = """
        UPDATE auditoria.revisiones_emergencia
        SET revisado_en = now(), revisado_por = 'sub-oficial', conclusion = 'JUSTIFICADO'
        WHERE id = @id
        """;

    [Fact]
    public async Task App_ShouldCloseAPendingReviewOfItsTenant_Once()
    {
        Guid id = await InsertRevisionAsync();
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        (await ExecuteAsync(conn, Cerrar, ("id", id))).ShouldBe(1);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn, Cerrar, ("id", id)));
        ex.MessageText.ShouldContain("ya está cerrada");
    }

    [Fact]
    public async Task ACloseWithoutAConclusion_ShouldViolateTheCheck()
    {
        Guid id = await InsertRevisionAsync();
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn,
            "UPDATE auditoria.revisiones_emergencia SET revisado_en = now(), revisado_por = 'x' WHERE id = @id", ("id", id)));

        ex.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("UPDATE auditoria.revisiones_emergencia SET motivo = motivo")]
    [InlineData("UPDATE auditoria.revisiones_emergencia SET vence_revision_en = vence_revision_en")]
    [InlineData("DELETE FROM auditoria.revisiones_emergencia")]
    public async Task App_ShouldNotTouchBeyondTheClosure(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn, sql));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task TheOwner_ShouldNotChangeTheReviewedFacts_NorDeleteIt()
    {
        Guid id = await InsertRevisionAsync();
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn,
            "UPDATE auditoria.revisiones_emergencia SET motivo = 'Otro motivo de veinte caracteres' WHERE id = @id", ("id", id))))
            .MessageText.ShouldContain("columnas del cierre");

        (await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn,
            "DELETE FROM auditoria.revisiones_emergencia WHERE id = @id", ("id", id))))
            .MessageText.ShouldContain("inmutable");
    }

    [Fact]
    public async Task AnotherTenant_ShouldNeitherSeeNorCloseIt()
    {
        Guid id = await InsertRevisionAsync();
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlB);

        (await ScalarAsync(conn, "SELECT count(*) FROM auditoria.revisiones_emergencia WHERE id = @id", ("id", id))).ShouldBe(0);
        (await ExecuteAsync(conn, Cerrar, ("id", id))).ShouldBe(0);
    }

    [Fact]
    public async Task AnActor_ShouldSeeOnlyItsOwnPendingReviews_ThroughAppSub()
    {
        Guid propia = await InsertRevisionAsync(Actor);
        Guid ajena = await InsertRevisionAsync("otro-actor");
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, ipsId: "110011234501");
        await ExecuteAsync(conn, "SELECT set_config('app.sub', @sub, false)", ("sub", Actor));

        (await ScalarAsync(conn, "SELECT count(*) FROM auditoria.revisiones_emergencia WHERE id = @id", ("id", propia))).ShouldBe(1);
        (await ScalarAsync(conn, "SELECT count(*) FROM auditoria.revisiones_emergencia WHERE id = @id", ("id", ajena))).ShouldBe(0);
    }
}
