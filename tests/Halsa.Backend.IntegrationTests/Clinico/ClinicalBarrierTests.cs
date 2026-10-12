using System.Security.Cryptography;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Clinico;

/// <summary>
/// Acceptance criteria of SPEC 03 for the database barriers: roles, grants, encryption at rest, the
/// clinical RLS and the immutability trigger. Raw <see cref="NpgsqlConnection"/>s prove the database
/// enforces them on its own, whatever the application does.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ClinicalBarrierTests(DatabaseFixture db)
{
    // ── Roles and schemas ─────────────────────────────────────────────────────

    [Fact]
    public async Task App_ShouldNotReachTheClinicoSchema()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "SELECT 1 FROM clinico.historia_clinica_detalle"));

        ex.MessageText.ShouldBe("permission denied for schema clinico");
    }

    [Fact]
    public async Task Clinico_ShouldNotReachTheAtelSchema()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "SELECT 1 FROM atel.casos_medicos"));

        ex.MessageText.ShouldBe("permission denied for schema atel");
    }

    // ── Encryption at rest ────────────────────────────────────────────────────

    [Fact]
    public async Task Contenido_ShouldNotBeStoredInClear()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid registro = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("FRACTURA-MARCADOR-7731"));

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);

        // The row is there and visible to this session, so a zero below is not an empty table.
        (await ScalarAsync(conn, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE id = @id", ("id", registro)))
            .ShouldBe(1);

        (await ScalarAsync(conn, """
            SELECT count(*) FROM clinico.historia_clinica_detalle
            WHERE position('FRACTURA-MARCADOR-7731'::bytea in contenido_ct) > 0
            """)).ShouldBe(0);
    }

    [Fact]
    public async Task CopiedContenido_ShouldFailGcmAuthentication()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid origen = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("Original"));
        Guid destino = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("Destino"));

        // As the owner, with the trigger disabled inside the transaction only: no other session
        // ever sees the table mutable.
        await using (NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA))
        {
            await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE clinico.historia_clinica_detalle DISABLE TRIGGER t_hc_inmutable;
                UPDATE clinico.historia_clinica_detalle d
                   SET contenido_ct = o.contenido_ct, contenido_nonce = o.contenido_nonce,
                       contenido_tag = o.contenido_tag, contenido_key_id = o.contenido_key_id
                  FROM clinico.historia_clinica_detalle o
                 WHERE o.id = @origen AND d.id = @destino;
                ALTER TABLE clinico.historia_clinica_detalle ENABLE TRIGGER t_hc_inmutable;
                """;
            cmd.Parameters.AddWithValue("origen", origen);
            cmd.Parameters.AddWithValue("destino", destino);
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
            await tx.CommitAsync();
        }

        // Same tenant, same DEK, same case: only the record id in the AAD differs.
        await Should.ThrowAsync<AuthenticationTagMismatchException>(() => SendAsync(
            db, FakeTenantContext.Arl(ArlA), new GetRegistrosClinicosQuery(new CasoMedicoId(caso))));
    }

    // ── Immutability ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UPDATE clinico.historia_clinica_detalle SET tipo = tipo WHERE id = @id")]
    [InlineData("DELETE FROM clinico.historia_clinica_detalle WHERE id = @id")]
    public async Task Clinico_UpdateOrDelete_ShouldBePermissionDenied(string sql)
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid registro = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("Inmutable"));

        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, sql, ("id", registro)));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("UPDATE clinico.historia_clinica_detalle SET tipo = tipo WHERE id = @id")]
    [InlineData("DELETE FROM clinico.historia_clinica_detalle WHERE id = @id")]
    public async Task Owner_UpdateOrDelete_ShouldBeRejectedByTheTrigger(string sql)
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid registro = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("Inmutable"));

        // With the tenant set the owner sees the row, so the statement reaches the trigger.
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, sql, ("id", registro)));

        ex.MessageText.ShouldContain("append-only");
    }

    // ── Clinical RLS ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Clinico_WithTenantA_ShouldNotSeeTenantBRecords()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);
        await RegistrarAsync(db, FakeTenantContext.Arl(ArlB), casoB, Contenido("De ARL_B"));

        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlA);

        (await ScalarAsync(conn, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE tenant_id = 'ARL_B'"))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Clinico_WithoutSessionVariables_ShouldSeeNoRecords()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), caso, Contenido("Sin contexto"));

        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString);

        (await ScalarAsync(conn, "SELECT count(*) FROM clinico.historia_clinica_detalle")).ShouldBe(0);
    }

    [Fact]
    public async Task Ips_InsertingWithAnotherTenantThanTheRemittedCase_ShouldViolateThePolicy()
    {
        Guid casoA = await InsertCasoAsync(db, ArlA);
        await InsertRemisionAsync(db, casoA, ArlA, Ips);

        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString, ipsId: Ips);

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO clinico.historia_clinica_detalle
              (id, tenant_id, caso_medico_id, tipo, profesional_sub, profesional_tipo_documento,
               profesional_numero_documento, profesional_registro_rethus, atendido_en, registrado_en,
               retener_hasta, contenido_ct, contenido_nonce, contenido_tag, contenido_key_id)
            VALUES
              (@id, 'ARL_B', @caso, 'ConceptoMedico', 'sub-test', 'CC', '1', 'RM-1', now(), now(),
               current_date + interval '15 years', '\x00', '\x00', '\x00', 'ARL_B:CONTENIDO:v1')
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("caso", casoA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());

        ex.MessageText.ShouldContain("new row violates row-level security policy");
    }

    [Fact]
    public async Task LlavesTenant_ShouldNotBeVisibleToTheApp()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "SELECT count(*) FROM clinico.llaves_tenant"));

        ex.MessageText.ShouldBe("permission denied for schema clinico");
    }

    // ── Blind index ───────────────────────────────────────────────────────────

    [Fact]
    public async Task IndiceCiego_ShouldMatchWithinATenant_AndDifferAcrossTenants()
    {
        var fractura = new CodigoClinico("CIE-10", "S62.6");

        Guid casoA = await InsertCasoAsync(db, ArlA);
        Guid casoB = await InsertCasoAsync(db, ArlB);

        Guid a1 = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), casoA, Contenido("Uno", fractura));
        // Same code, written differently: the index normalises case and spaces.
        Guid a2 = await RegistrarAsync(db, FakeTenantContext.Arl(ArlA), casoA,
            Contenido("Dos", new CodigoClinico("cie-10", " s62.6 ")));
        Guid b1 = await RegistrarAsync(db, FakeTenantContext.Arl(ArlB), casoB, Contenido("Tres", fractura));

        const string sql = "SELECT diagnostico_indice_ciego FROM clinico.historia_clinica_detalle WHERE id = @id";

        byte[] indiceA1, indiceA2, indiceB1;
        await using (NpgsqlConnection connA = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlA))
        {
            indiceA1 = await BytesAsync(connA, sql, a1);
            indiceA2 = await BytesAsync(connA, sql, a2);
        }

        await using (NpgsqlConnection connB = await OpenAsync(db.ClinicalConnectionString, tenantId: ArlB))
        {
            indiceB1 = await BytesAsync(connB, sql, b1);
        }

        indiceA1.Length.ShouldBe(32);
        indiceA2.ShouldBe(indiceA1);
        indiceB1.ShouldNotBe(indiceA1);
    }
}
