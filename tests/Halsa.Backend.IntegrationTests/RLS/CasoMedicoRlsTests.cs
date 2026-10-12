using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.IntegrationTests.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.RLS;

/// <summary>
/// Acceptance criteria of SPEC 02 for the database barriers: roles, grants and RLS on
/// <c>atel.casos_medicos</c>. Raw <see cref="NpgsqlConnection"/>s prove the database enforces
/// isolation on its own; the last two tests go through EF with the real interceptor.
///
/// Every test seeds its own rows (fresh ids and radicados), so assertions are scoped to them
/// and the tests can share the container in any order.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CasoMedicoRlsTests(DatabaseFixture db)
{
    private const string ArlA = "ARL_A";
    private const string ArlB = "ARL_B";
    private const string Ips = "110011234501";
    private const string OtraIps = "110011234599";

    // ── Criteria ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task App_WithTenantA_ShouldNotSeeTenantBRows()
    {
        await InsertCasoAsync(ArlB);

        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        long count = await ScalarAsync(conn, "SELECT count(*) FROM atel.casos_medicos WHERE tenant_id = 'ARL_B'");

        count.ShouldBe(0);
    }

    [Fact]
    public async Task App_WithoutSessionVariables_ShouldSeeNoRows()
    {
        await InsertCasoAsync(ArlA);

        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString);

        long count = await ScalarAsync(conn, "SELECT count(*) FROM atel.casos_medicos");

        count.ShouldBe(0);
    }

    [Fact]
    public async Task App_WithIps_ShouldSeeOnlyCasesWithVigenteRemision_FromBothTenants()
    {
        Guid remitidoA = await InsertCasoAsync(ArlA);
        Guid sinRemitirA = await InsertCasoAsync(ArlA);
        Guid remitidoB = await InsertCasoAsync(ArlB);
        Guid remitidoOtraIpsB = await InsertCasoAsync(ArlB);

        Guid remisionA = await InsertRemisionAsync(remitidoA, ArlA, Ips);
        await InsertRemisionAsync(remitidoB, ArlB, Ips);
        await InsertRemisionAsync(remitidoOtraIpsB, ArlB, OtraIps);

        Guid[] mios = [remitidoA, sinRemitirA, remitidoB, remitidoOtraIpsB];

        (await VisibleToIpsAsync(mios)).ShouldBe([remitidoA, remitidoB], ignoreOrder: true);

        await RevocarRemisionAsync(remisionA, ArlA);

        (await VisibleToIpsAsync(mios)).ShouldBe([remitidoB]);
    }

    [Fact]
    public async Task App_InsertingForAnotherTenant_ShouldViolateThePolicy()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => InsertCasoAsync(conn, Guid.CreateVersion7(), ArlB));

        ex.MessageText.ShouldContain("new row violates row-level security policy");
    }

    [Fact]
    public async Task App_Delete_ShouldBePermissionDenied()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(
            () => ScalarAsync(conn, "DELETE FROM atel.casos_medicos"));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        ex.MessageText.ShouldContain("permission denied");
    }

    [Fact]
    public async Task Owner_WithoutSessionVariables_ShouldSeeNoRows()
    {
        await InsertCasoAsync(ArlA);

        await using (NpgsqlConnection withTenant = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA))
        {
            // The rows exist: the owner sees them once the tenant is set.
            (await ScalarAsync(withTenant, "SELECT count(*) FROM atel.casos_medicos")).ShouldBeGreaterThan(0);
        }

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString);

        long count = await ScalarAsync(conn, "SELECT count(*) FROM atel.casos_medicos");

        count.ShouldBe(0);
    }

    [Fact]
    public async Task PooledConnection_ReusedWithoutContext_ShouldSeeNoRows()
    {
        Guid casoA = await InsertCasoAsync(ArlA);

        // A pool of one guarantees the second context gets the same physical connection.
        string pooled = $"{db.AppConnectionString};Pooling=true;Maximum Pool Size=1;Application Name=rls-pool-test";

        int firstPid;
        await using (ApplicationDbContext arl = db.CreateDbContext(FakeTenantContext.Arl(ArlA), pooled))
        {
            await arl.Database.OpenConnectionAsync();
            firstPid = (int)await ScalarAsync((NpgsqlConnection)arl.Database.GetDbConnection(), "SELECT pg_backend_pid()");

            (await arl.CasosMedicos.CountAsync(c => c.Id == new CasoMedicoId(casoA))).ShouldBe(1);

            await arl.Database.CloseConnectionAsync();
        }

        await using ApplicationDbContext anonimo = db.CreateDbContext(NullTenantContext.Instance, pooled);
        await anonimo.Database.OpenConnectionAsync();
        var conn = (NpgsqlConnection)anonimo.Database.GetDbConnection();

        ((int)await ScalarAsync(conn, "SELECT pg_backend_pid()")).ShouldBe(firstPid);
        (await ScalarAsync(conn, "SELECT count(*) FROM atel.casos_medicos")).ShouldBe(0);
    }

    [Fact]
    public async Task IgnoreQueryFilters_WithTenantA_ShouldStillHideTenantB()
    {
        Guid casoB = await InsertCasoAsync(ArlB);

        await using ApplicationDbContext arl = db.CreateDbContext(FakeTenantContext.Arl(ArlA));

        bool visible = await arl.CasosMedicos
            .IgnoreQueryFilters()
            .AnyAsync(c => c.Id == new CasoMedicoId(casoB));

        visible.ShouldBeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid[]> VisibleToIpsAsync(Guid[] ids)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, ipsId: Ips);

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM atel.casos_medicos WHERE id = ANY(@ids)";
        cmd.Parameters.AddWithValue("ids", ids);

        var visibles = new List<Guid>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            visibles.Add(reader.GetGuid(0));
        }

        return [.. visibles];
    }

    /// <summary>Seeds a case as <c>halsa_owner</c>. RLS is forced, so the owner sets the tenant first.</summary>
    private async Task<Guid> InsertCasoAsync(string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);
        Guid id = Guid.CreateVersion7();
        await InsertCasoAsync(conn, id, tenantId);
        return id;
    }

    private static async Task InsertCasoAsync(NpgsqlConnection conn, Guid id, string tenantId)
    {
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
        cmd.Parameters.AddWithValue("rad", $"RLS-{id:N}");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> InsertRemisionAsync(Guid casoId, string tenantId, string ipsId)
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

    private async Task RevocarRemisionAsync(Guid remisionId, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE atel.remisiones SET revocada_en = now(), revocada_por = 'sub-test' WHERE id = @id";
        cmd.Parameters.AddWithValue("id", remisionId);
        (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    /// <summary>Opens a connection and, if given, sets the session variables the policies read.</summary>
    private static async Task<NpgsqlConnection> OpenAsync(
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

    private static async Task<long> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
