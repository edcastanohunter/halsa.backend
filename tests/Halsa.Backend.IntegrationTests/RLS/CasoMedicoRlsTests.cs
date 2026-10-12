using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;

namespace Halsa.Backend.IntegrationTests.RLS;

/// <summary>
/// Verifies that the PostgreSQL RLS policies on <c>atel.caso_medico</c> isolate rows
/// by tenant and IPS without relying on any EF Core query filter.
///
/// Tests open a raw <see cref="NpgsqlConnection"/> as <c>halsa_app</c>, set
/// <c>app.tenant_id</c> / <c>app.ips_id</c> directly via <c>set_config</c>, and
/// execute plain SQL. This proves the database-level enforcement is correct
/// independently of the application layer.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CasoMedicoRlsTests(DatabaseFixture db)
{
    private static readonly Guid CasoId1  = Guid.NewGuid();
    private static readonly Guid CasoId2  = Guid.NewGuid();
    private static readonly Guid RemisionId1 = Guid.NewGuid();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NpgsqlConnection OpenAppConnection() =>
        new(db.AppConnectionString);

    private NpgsqlConnection OpenOwnerConnection() =>
        new(db.OwnerConnectionString);

    /// <summary>Seeds two caso_medico rows belonging to different tenants.</summary>
    private async Task SeedAsync()
    {
        await using NpgsqlConnection conn = OpenOwnerConnection();
        await conn.OpenAsync();

        // Delete any rows left from previous test runs (xUnit may reuse containers).
        await using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM atel.caso_medico WHERE id IN (@id1, @id2)";
            del.Parameters.AddWithValue("id1", CasoId1);
            del.Parameters.AddWithValue("id2", CasoId2);
            await del.ExecuteNonQueryAsync();
        }

        const string insertCaso = """
            INSERT INTO atel.caso_medico
              (id, tenant_id, radicado, tipo_evento, fecha_evento,
               trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
               municipio_divipola, estado, es_grave_o_mortal, radicado_en)
            VALUES
              (@id, @tid, @rad, 1, NOW(), 'CC', '123456', 'NIT', '9001', '11001', 0, false, NOW())
            """;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = insertCaso;
            cmd.Parameters.AddWithValue("id",  CasoId1);
            cmd.Parameters.AddWithValue("tid", "arl-sura");
            cmd.Parameters.AddWithValue("rad", "RAD-RLS-001");
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = insertCaso;
            cmd.Parameters.AddWithValue("id",  CasoId2);
            cmd.Parameters.AddWithValue("tid", "arl-positiva");
            cmd.Parameters.AddWithValue("rad", "RAD-RLS-002");
            await cmd.ExecuteNonQueryAsync();
        }

        // Add a vigente remision to Ips 110011234501 for CasoId2 (so an IPS actor can see it).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO atel.remision
                  (id, caso_medico_id, ips_id, remitido_por, remitido_en)
                VALUES
                  (@rid, @cid, '110011234501', 'sub-test', NOW())
                ON CONFLICT DO NOTHING
                """;
            cmd.Parameters.AddWithValue("rid", RemisionId1);
            cmd.Parameters.AddWithValue("cid", CasoId2);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArlActor_ShouldSeeOnlyOwnTenantCasos()
    {
        await SeedAsync();

        await using NpgsqlConnection conn = OpenAppConnection();
        await conn.OpenAsync();

        // Identify as arl-sura.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SELECT set_config('app.tenant_id', 'arl-sura', false), set_config('app.ips_id', '', false)";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT id FROM atel.caso_medico WHERE id IN (@id1, @id2)";
        query.Parameters.AddWithValue("id1", CasoId1);
        query.Parameters.AddWithValue("id2", CasoId2);

        var visible = new List<Guid>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            visible.Add(reader.GetGuid(0));
        }

        visible.ShouldContain(CasoId1);       // own tenant
        visible.ShouldNotContain(CasoId2);    // different tenant — hidden by RLS
    }

    [Fact]
    public async Task ArlActor_ShouldNotSeeOtherTenantCasos()
    {
        await SeedAsync();

        await using NpgsqlConnection conn = OpenAppConnection();
        await conn.OpenAsync();

        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SELECT set_config('app.tenant_id', 'arl-positiva', false), set_config('app.ips_id', '', false)";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT id FROM atel.caso_medico WHERE id = @id1";
        query.Parameters.AddWithValue("id1", CasoId1);

        await using var reader = await query.ExecuteReaderAsync();
        bool found = await reader.ReadAsync();

        found.ShouldBeFalse("arl-positiva should not see arl-sura's case");
    }

    [Fact]
    public async Task IpsActor_ShouldSeeCasoWithVigenteRemision()
    {
        await SeedAsync();

        await using NpgsqlConnection conn = OpenAppConnection();
        await conn.OpenAsync();

        // IPS actor: no tenant_id, only ips_id.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SELECT set_config('app.tenant_id', '', false), set_config('app.ips_id', '110011234501', false)";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT id FROM atel.caso_medico WHERE id = @id";
        query.Parameters.AddWithValue("id", CasoId2);

        await using var reader = await query.ExecuteReaderAsync();
        bool found = await reader.ReadAsync();

        found.ShouldBeTrue("IPS with vigente remisión should see the case");
    }

    [Fact]
    public async Task IpsActor_ShouldNotSeeCasoWithoutRemision()
    {
        await SeedAsync();

        await using NpgsqlConnection conn = OpenAppConnection();
        await conn.OpenAsync();

        // IPS 110011234502 has no remision to CasoId1.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = "SELECT set_config('app.tenant_id', '', false), set_config('app.ips_id', '110011234502', false)";
            await setCmd.ExecuteNonQueryAsync();
        }

        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT id FROM atel.caso_medico WHERE id IN (@id1, @id2)";
        query.Parameters.AddWithValue("id1", CasoId1);
        query.Parameters.AddWithValue("id2", CasoId2);

        await using var reader = await query.ExecuteReaderAsync();
        bool found = await reader.ReadAsync();

        found.ShouldBeFalse("IPS without remisión should not see any case");
    }

    [Fact]
    public async Task NoSessionVars_ShouldSeeNoCasos()
    {
        await SeedAsync();

        await using NpgsqlConnection conn = OpenAppConnection();
        await conn.OpenAsync();

        // No set_config call — both vars are empty string by default.
        // RLS: tenant_id = '' matches nothing; IPS EXISTS subquery returns false.
        await using var query = conn.CreateCommand();
        query.CommandText = "SELECT id FROM atel.caso_medico WHERE id IN (@id1, @id2)";
        query.Parameters.AddWithValue("id1", CasoId1);
        query.Parameters.AddWithValue("id2", CasoId2);

        await using var reader = await query.ExecuteReaderAsync();
        bool found = await reader.ReadAsync();

        found.ShouldBeFalse("with no session vars, no case should be visible");
    }
}
