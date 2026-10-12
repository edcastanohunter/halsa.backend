using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Clinico.Commands.Registrar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.Clinico;

/// <summary>
/// Seeding and raw-SQL helpers for the SPEC 03 tests. Cases and remissions are seeded as
/// <c>halsa_owner</c> (RLS is forced, so the owner sets the tenant first); clinical records go
/// through the real command so they are encrypted exactly as in production.
/// </summary>
internal static class ClinicoSql
{
    public const string ArlA = "ARL_A";
    public const string ArlB = "ARL_B";
    public const string Ips = "110011234501";

    public static readonly DatosProfesional Medico = new("CC", "12345678", "RM-12345");

    public static ContenidoClinico Contenido(string motivo, params CodigoClinico[] diagnosticos) =>
        new(motivo, "Anamnesis", "Examen físico", "Análisis", "Plan", diagnosticos, null);

    // ── Commands ──────────────────────────────────────────────────────────────

    public static async Task<Result<TResponse>> SendAsync<TResponse>(
        DatabaseFixture db, FakeTenantContext actor, IRequest<Result<TResponse>> request)
    {
        await using AsyncServiceScope scope = db.CreateScope(actor);
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    public static async Task<Guid> RegistrarAsync(
        DatabaseFixture db, FakeTenantContext actor, Guid casoId, ContenidoClinico contenido)
    {
        Result<Guid> result = await SendAsync(db, actor, new RegistrarRegistroClinicoCommand(
            new CasoMedicoId(casoId), TipoRegistroClinico.ConceptoMedico,
            DateTimeOffset.UtcNow.AddHours(-1), contenido, Medico));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    // ── atel seed (as halsa_owner) ────────────────────────────────────────────

    public static async Task<Guid> InsertCasoAsync(DatabaseFixture db, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.casos_medicos
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en)
            VALUES
              (@id, @tid, @rad, 'AccidenteTrabajo', now(), 'CC', '123456', 'NIT', '9001', '11001',
               'Radicado', false, now())
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("rad", $"HC-{id:N}");
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    public static async Task<Guid> InsertRemisionAsync(DatabaseFixture db, Guid casoId, string tenantId, string ipsId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
            VALUES (@id, @caso, @tid, @ips, now(), 'sub-test')
            """;
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("caso", casoId);
        cmd.Parameters.AddWithValue("tid", tenantId);
        cmd.Parameters.AddWithValue("ips", ipsId);
        await cmd.ExecuteNonQueryAsync();

        return id;
    }

    public static async Task RevocarRemisionAsync(DatabaseFixture db, Guid remisionId, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE atel.remisiones SET revocada_en = now(), revocada_por = 'sub-test' WHERE id = @id";
        cmd.Parameters.AddWithValue("id", remisionId);
        (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    // ── Raw SQL ───────────────────────────────────────────────────────────────

    /// <summary>Opens a connection and, if given, sets the session variables the policies read.</summary>
    public static async Task<NpgsqlConnection> OpenAsync(
        string connectionString,
        string? tenantId = null,
        string? ipsId = null)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        if (tenantId is not null || ipsId is not null)
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT set_config('app.tenant_id', @tid, false), set_config('app.ips_id', @ips, false)";
            cmd.Parameters.AddWithValue("tid", tenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("ips", ipsId ?? string.Empty);
            await cmd.ExecuteNonQueryAsync();
        }

        return conn;
    }

    public static async Task<long> ScalarAsync(
        NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        object? result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<byte[]> BytesAsync(NpgsqlConnection conn, string sql, Guid id)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }
}
