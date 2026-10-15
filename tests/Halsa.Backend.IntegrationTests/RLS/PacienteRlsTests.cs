using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.RLS;

/// <summary>
/// SPEC 12, step 1: the patient's RLS on <c>atel.casos_medicos</c> and <c>atel.remisiones</c>, the links by document, and
/// <c>atel.canjear_codigo_activacion</c>. Raw <see cref="NpgsqlConnection"/>s prove the database enforces it on its own;
/// the last test goes through EF with the real interceptor and the query filter.
///
/// Every test uses its own worker document and its own patient <c>sub</c>, so the shared container's other rows (which
/// all use <c>CC 123456</c>) never match.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PacienteRlsTests(DatabaseFixture db)
{
    private const string ArlA = "ARL_A";
    private const string ArlB = "ARL_B";
    private const string Ips = "110011234501";

    // ── Criteria ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task App_AsPacienteWithoutVinculo_ShouldCountNoCases()
    {
        await InsertCasoAsync(ArlA, NuevoDocumento());

        await using NpgsqlConnection conn = await OpenPacienteAsync(db.AppConnectionString, NuevoSub());

        (await ScalarAsync(conn, "SELECT count(*) FROM atel.casos_medicos")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.remisiones")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.incapacidades")).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.dictamenes_pcl")).ShouldBe(0);
    }

    [Fact]
    public async Task Canje_ShouldLinkTheDocument_AndShowItsCasesAndRemisiones_FromEveryTenant()
    {
        string documento = NuevoDocumento();
        Guid casoA = await InsertCasoAsync(ArlA, documento);
        Guid casoB = await InsertCasoAsync(ArlB, documento);
        Guid otroDocumento = await InsertCasoAsync(ArlA, NuevoDocumento());
        await InsertRemisionAsync(casoB, ArlB);
        await InsertRemisionAsync(otroDocumento, ArlA);

        (string id, string secreto) = await InsertCodigoAsync(ArlA, casoA, documento);
        string sub = NuevoSub();

        Canje canje = await CanjearAsync(sub, id, secreto);

        canje.Resultado.ShouldBe("VINCULADO");
        canje.TenantId.ShouldBe(ArlA);
        canje.CasoMedicoId.ShouldBe(casoA);
        canje.TrabajadorNumero.ShouldBe(documento);

        await using NpgsqlConnection conn = await OpenPacienteAsync(db.AppConnectionString, sub);
        (await IdsAsync(conn, "SELECT id FROM atel.casos_medicos")).ShouldBe([casoA, casoB], ignoreOrder: true);
        (await IdsAsync(conn, "SELECT caso_medico_id FROM atel.remisiones")).ShouldBe([casoB]);
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.vinculos_paciente")).ShouldBe(1);

        (await EstadoCodigoAsync(ArlA, id)).ShouldBe("USADO");

        // Another patient sees nothing of it.
        await using NpgsqlConnection otro = await OpenPacienteAsync(db.AppConnectionString, NuevoSub());
        (await ScalarAsync(otro, "SELECT count(*) FROM atel.vinculos_paciente")).ShouldBe(0);
        (await ScalarAsync(otro, $"SELECT count(*) FROM atel.casos_medicos WHERE id = '{casoA}'")).ShouldBe(0);
    }

    [Fact]
    public async Task Canje_OfAUsedAnAbsentAndAnExpiredCode_ShouldAllBeInvalido()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);

        (string usado, string secretoUsado) = await InsertCodigoAsync(ArlA, caso, documento);
        (await CanjearAsync(NuevoSub(), usado, secretoUsado)).Resultado.ShouldBe("VINCULADO");

        (await CanjearAsync(NuevoSub(), usado, secretoUsado)).Resultado.ShouldBe("INVALIDO");
        (await CanjearAsync(NuevoSub(), FormatoCodigoActivacion.Aleatorio(6), secretoUsado)).Resultado.ShouldBe("INVALIDO");

        Guid otroCaso = await InsertCasoAsync(ArlA, documento);
        (string vencido, string secretoVencido) = await InsertCodigoAsync(ArlA, otroCaso, documento, venceEnDias: -1);
        (await CanjearAsync(NuevoSub(), vencido, secretoVencido)).Resultado.ShouldBe("INVALIDO");
        (await EstadoCodigoAsync(ArlA, vencido)).ShouldBe("EXPIRADO");
    }

    [Fact]
    public async Task FiveWrongSecrets_ShouldBlockTheCode_AndTheRightOneAfterwardsIsBloqueado()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);
        (string id, string secreto) = await InsertCodigoAsync(ArlA, caso, documento);

        for (int i = 0; i < CodigoActivacion.MaxIntentosFallidos; i++)
        {
            (await CanjearAsync(NuevoSub(), id, "0000000000")).Resultado.ShouldBe("INVALIDO");
        }

        (await EstadoCodigoAsync(ArlA, id)).ShouldBe("BLOQUEADO");
        (await CanjearAsync(NuevoSub(), id, secreto)).Resultado.ShouldBe("BLOQUEADO");
    }

    [Fact]
    public async Task FiveFailuresOfAPatient_ShouldRefuseTheSixth_EvenWithAValidCode()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);
        (string id, string secreto) = await InsertCodigoAsync(ArlA, caso, documento);
        string sub = NuevoSub();

        for (int i = 0; i < 5; i++)
        {
            (await CanjearAsync(sub, FormatoCodigoActivacion.Aleatorio(6), secreto)).Resultado.ShouldBe("INVALIDO");
        }

        (await CanjearAsync(sub, id, secreto)).Resultado.ShouldBe("DEMASIADOS_INTENTOS");
        (await EstadoCodigoAsync(ArlA, id)).ShouldBe("ACTIVO");
    }

    [Fact]
    public async Task Canje_ForAnotherDocument_ShouldBeVinculoDistinto_AndKeepTheCodeActivo()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);
        (string id, string secreto) = await InsertCodigoAsync(ArlA, caso, documento);
        string sub = NuevoSub();

        string otroDocumento = NuevoDocumento();
        Guid otroCaso = await InsertCasoAsync(ArlB, otroDocumento);
        (string otro, string secretoOtro) = await InsertCodigoAsync(ArlB, otroCaso, otroDocumento);
        (await CanjearAsync(sub, otro, secretoOtro)).Resultado.ShouldBe("VINCULADO");

        (await CanjearAsync(sub, id, secreto)).Resultado.ShouldBe("VINCULO_DISTINTO");
        (await EstadoCodigoAsync(ArlA, id)).ShouldBe("ACTIVO");
    }

    [Fact]
    public async Task App_AsPaciente_ShouldNotReadCodesNorAttempts()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);
        (string id, _) = await InsertCodigoAsync(ArlA, caso, documento);
        string sub = NuevoSub();

        await using NpgsqlConnection conn = await OpenPacienteAsync(db.AppConnectionString, sub);

        // Even setting the function's variable: its policy is TO halsa_owner.
        await ScalarAsync(conn, $"SELECT count(*) FROM (SELECT set_config('app.canje_codigo', '{id}', false)) x");
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.codigos_activacion")).ShouldBe(0);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "SELECT count(*) FROM atel.intentos_canje"));
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        PostgresException insert = await Should.ThrowAsync<PostgresException>(() => ScalarAsync(conn, $"""
            INSERT INTO atel.vinculos_paciente (paciente_sub, trabajador_tipo, trabajador_numero, codigo_id, vinculado_en)
            VALUES ('{sub}', 'CC', '{documento}', '{id}', now())
            """));
        insert.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Canje_OutsideTheSessionOfTheSamePatient_ShouldBeRefused()
    {
        string documento = NuevoDocumento();
        Guid caso = await InsertCasoAsync(ArlA, documento);
        (string id, string secreto) = await InsertCodigoAsync(ArlA, caso, documento);

        // An ARL session.
        await using (NpgsqlConnection arl = await OpenAsync(db.AppConnectionString, tenantId: ArlA))
        {
            PostgresException ex = await Should.ThrowAsync<PostgresException>(() => CanjearAsync(arl, NuevoSub(), id, secreto));
            ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // A patient session redeeming for another sub.
        await using NpgsqlConnection paciente = await OpenPacienteAsync(db.AppConnectionString, NuevoSub());
        PostgresException otroSub = await Should.ThrowAsync<PostgresException>(
            () => CanjearAsync(paciente, NuevoSub(), id, secreto));
        otroSub.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);

        (await EstadoCodigoAsync(ArlA, id)).ShouldBe("ACTIVO");
    }

    [Fact]
    public async Task QueryFilter_AsPaciente_ShouldReturnTheLinkedCases_AndRlsStillHidesTheRest()
    {
        string documento = NuevoDocumento();
        Guid casoA = await InsertCasoAsync(ArlA, documento);
        Guid casoB = await InsertCasoAsync(ArlB, documento);
        Guid ajeno = await InsertCasoAsync(ArlA, NuevoDocumento());
        (string id, string secreto) = await InsertCodigoAsync(ArlA, casoA, documento);
        string sub = NuevoSub();
        (await CanjearAsync(sub, id, secreto)).Resultado.ShouldBe("VINCULADO");

        await using ApplicationDbContext paciente = db.CreateDbContext(FakeTenantContext.Paciente(sub));

        CasoMedicoId[] mios = [new(casoA), new(casoB), new(ajeno)];
        List<Guid> visibles = await paciente.CasosMedicos
            .Where(c => mios.Contains(c.Id))
            .Select(c => c.Id.Value)
            .ToListAsync();
        visibles.ShouldBe([casoA, casoB], ignoreOrder: true);

        (await paciente.CasosMedicos.IgnoreQueryFilters().AnyAsync(c => c.Id == new CasoMedicoId(ajeno))).ShouldBeFalse();
        (await paciente.VinculosPaciente.CountAsync()).ShouldBe(1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record Canje(string Resultado, string? TenantId, Guid? CasoMedicoId, string? TrabajadorNumero);

    private static string NuevoSub() => Guid.CreateVersion7().ToString();

    /// <summary>A worker document no other test uses.</summary>
    private static string NuevoDocumento() => $"9{Random.Shared.NextInt64(100_000_000_000, 999_999_999_999)}";

    private async Task<Canje> CanjearAsync(string sub, string id, string secreto)
    {
        await using NpgsqlConnection conn = await OpenPacienteAsync(db.AppConnectionString, sub);
        return await CanjearAsync(conn, sub, id, secreto);
    }

    private static async Task<Canje> CanjearAsync(NpgsqlConnection conn, string sub, string id, string secreto)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT resultado, tenant_id, caso_medico_id, trabajador_numero
            FROM atel.canjear_codigo_activacion(@id, @secreto, @sub)
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("secreto", secreto);
        cmd.Parameters.AddWithValue("sub", sub);

        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        return new Canje(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>Seeds a <c>Validado</c> case as <c>halsa_owner</c> with the tenant set.</summary>
    private async Task<Guid> InsertCasoAsync(string tenantId, string documento)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);
        Guid id = Guid.CreateVersion7();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.casos_medicos
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en, validado_en)
            VALUES
              (@id, @tid, @rad, 'AccidenteTrabajo', now(), 'CC', @doc, 'NIT', '9001', '11001',
               'Validado', false, now(), now())
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("rad", $"PAC-{id:N}");
        cmd.Parameters.AddWithValue("doc", documento);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    private async Task InsertRemisionAsync(Guid casoId, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
            VALUES (@id, @caso, @tid, @ips, now(), 'sub-test')
            """;
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("caso", casoId);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("ips", Ips);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Issues an <c>ACTIVO</c> code as the API would: only the salt and the hash of the secret are stored.</summary>
    private async Task<(string Id, string Secreto)> InsertCodigoAsync(
        string tenantId, Guid casoId, string documento, int venceEnDias = CodigoActivacion.VigenciaDias)
    {
        string id = FormatoCodigoActivacion.Aleatorio(FormatoCodigoActivacion.LargoId);
        string secreto = FormatoCodigoActivacion.Aleatorio(FormatoCodigoActivacion.LargoSecreto);
        byte[] sal = FormatoCodigoActivacion.NuevaSal();

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.codigos_activacion
              (id, tenant_id, caso_medico_id, trabajador_tipo, trabajador_numero, secreto_hash, secreto_sal,
               emitido_en, emitido_por, expira_en, intentos_fallidos, estado)
            VALUES
              (@id, @tid, @caso, 'CC', @doc, @hash, @sal, now() - interval '31 days', 'sub-analista',
               now() + make_interval(days => @dias), 0, 'ACTIVO')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("caso", casoId);
        cmd.Parameters.AddWithValue("doc", documento);
        cmd.Parameters.AddWithValue("hash", FormatoCodigoActivacion.Hash(sal, secreto));
        cmd.Parameters.AddWithValue("sal", sal);
        cmd.Parameters.AddWithValue("dias", venceEnDias);
        await cmd.ExecuteNonQueryAsync();

        return (id, secreto);
    }

    private async Task<string> EstadoCodigoAsync(string tenantId, string id)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT estado FROM atel.codigos_activacion WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, string? tenantId = null)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        if (tenantId is not null)
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT set_config('app.tenant_id', @tid, false), set_config('app.actor_tipo', 'arl', false)";
            cmd.Parameters.AddWithValue("tid", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }

    /// <summary>A session as the interceptor leaves it for a patient: no tenant, no IPS, <c>paciente</c> and its sub.</summary>
    private static async Task<NpgsqlConnection> OpenPacienteAsync(string connectionString, string sub)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.tenant_id', '', false), set_config('app.ips_id', '', false),
                   set_config('app.actor_tipo', 'paciente', false), set_config('app.sub', @sub, false)
            """;
        cmd.Parameters.AddWithValue("sub", sub);
        await cmd.ExecuteNonQueryAsync();

        return conn;
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<Guid>> IdsAsync(NpgsqlConnection conn, string sql)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var ids = new List<Guid>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
